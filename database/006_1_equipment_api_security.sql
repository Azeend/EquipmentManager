-- Этап 6.1: однократная миграция серверного CRUD оборудования. НЕ ВЫПОЛНЯЛАСЬ.
-- Запускать вручную владельцем схемы после аудита инструкции equipment_api_setup.md.
-- Не переносит данные и не включает PostgreSQL-режим приложения.
BEGIN;

DO $$
BEGIN
    IF current_setting('server_version_num')::integer < 170000 THEN
        RAISE EXCEPTION 'Миграция подготовлена для PostgreSQL 17 и новее';
    END IF;
    IF current_database() <> 'postgres'
        OR to_regclass('public.profiles') IS NULL
        OR to_regclass('public.equipment') IS NULL
        OR to_regclass('public.faults') IS NULL
        OR to_regclass('public.maintenance_records') IS NULL THEN
        RAISE EXCEPTION 'Требуется проверенная схема EquipmentManager в базе postgres';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = 'equipment_api')
        OR EXISTS (SELECT 1 FROM pg_catalog.pg_namespace WHERE nspname = 'equipment_api_security') THEN
        RAISE EXCEPTION 'Роль или служебная схема уже существуют; повторная установка запрещена';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles
        WHERE rolname = 'equipment_app' AND NOT (rolsuper OR rolbypassrls)) THEN
        RAISE EXCEPTION 'Ожидается существующая ограниченная equipment_app; её права не изменяются';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_catalog.pg_class
        WHERE oid IN ('public.profiles'::regclass, 'public.equipment'::regclass,
            'public.faults'::regclass, 'public.maintenance_records'::regclass)
          AND (NOT relrowsecurity OR relkind <> 'r')) THEN
        RAISE EXCEPTION 'Ожидаются обычные таблицы с уже включённым RLS';
    END IF;
    -- Новая роль ни в какие группы не входит. Политики других именованных ролей
    -- к ней неприменимы; общие PUBLIC-политики требуют отдельного разбора.
    IF EXISTS (SELECT 1 FROM pg_catalog.pg_policy
        WHERE polrelid IN ('public.profiles'::regclass, 'public.equipment'::regclass,
            'public.faults'::regclass, 'public.maintenance_records'::regclass)
          AND 0::oid = ANY(polroles)) THEN
        RAISE EXCEPTION 'Обнаружены PUBLIC RLS-политики: требуется отдельный аудит';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_catalog.pg_trigger
        WHERE tgrelid = 'public.equipment'::regclass AND NOT tgisinternal) THEN
        RAISE EXCEPTION 'Неожиданные триггеры оборудования требуют отдельного аудита';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_constraint
        WHERE conrelid = 'public.equipment'::regclass AND contype = 'u' AND convalidated
          AND conname = 'equipment_inventory_number_unique'
          AND conkey = ARRAY[(SELECT attnum FROM pg_catalog.pg_attribute
              WHERE attrelid = 'public.equipment'::regclass
                AND attname = 'normalized_inventory_number')]::smallint[])
        OR (SELECT count(*) FROM pg_catalog.pg_constraint
            WHERE contype = 'f' AND convalidated AND confdeltype = 'r'
              AND confrelid = 'public.equipment'::regclass
              AND conrelid IN ('public.faults'::regclass,
                  'public.maintenance_records'::regclass)) <> 2 THEN
        RAISE EXCEPTION 'Не подтверждены исходные UNIQUE и FK ON DELETE RESTRICT';
    END IF;
    IF (SELECT count(*) FROM pg_catalog.pg_trigger
        WHERE NOT tgisinternal
          AND tgrelid IN ('public.faults'::regclass, 'public.maintenance_records'::regclass)) <> 3
        OR (SELECT count(*) FROM pg_catalog.pg_trigger
            WHERE NOT tgisinternal AND tgenabled = 'O'
              AND ((tgrelid = 'public.faults'::regclass AND tgname = 'faults_guard'
                    AND tgfoid = 'public.guard_fault()'::regprocedure)
                OR (tgrelid = 'public.faults'::regclass AND tgname = 'faults_create_history'
                    AND tgfoid = 'public.create_maintenance_record()'::regprocedure)
                OR (tgrelid = 'public.maintenance_records'::regclass AND tgname = 'maintenance_records_guard'
                    AND tgfoid = 'public.guard_maintenance_record()'::regprocedure))) <> 3
        OR EXISTS (SELECT 1 FROM pg_catalog.pg_proc
            WHERE oid IN ('public.trim_whitespace(text)'::regprocedure,
                'public.guard_fault()'::regprocedure,
                'public.create_maintenance_record()'::regprocedure,
                'public.guard_maintenance_record()'::regprocedure)
              AND (prosecdef OR NOT coalesce(proconfig @> ARRAY['search_path=pg_catalog'], false))) THEN
        RAISE EXCEPTION 'Ожидаются исходные включённые SECURITY INVOKER триггеры и безопасный search_path';
    END IF;
END;
$$;

-- Без пароля; пароль назначается отдельно через интерактивную команду psql \password.
CREATE ROLE equipment_api LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE
    NOINHERIT NOREPLICATION NOBYPASSRLS;

CREATE SCHEMA equipment_api_security;
REVOKE ALL ON SCHEMA equipment_api_security FROM PUBLIC, anon, authenticated;

-- GUC — доверенный контекст сервера, а НЕ механизм проверки JWT в PostgreSQL.
-- Пустой/отсутствующий контекст даёт NULL, некорректный UUID — отказ запроса.
CREATE FUNCTION equipment_api_security.current_profile_id() RETURNS uuid
LANGUAGE sql STABLE SECURITY INVOKER SET search_path = pg_catalog
AS $$ SELECT nullif(current_setting('equipment.user_id', true), '')::uuid $$;
REVOKE ALL ON FUNCTION equipment_api_security.current_profile_id() FROM PUBLIC, anon, authenticated;

-- Нет SECURITY DEFINER и рекурсии: политика profiles ниже не вызывает эту функцию.
CREATE FUNCTION equipment_api_security.has_any_role(allowed_roles text[]) RETURNS boolean
LANGUAGE sql STABLE SECURITY INVOKER SET search_path = pg_catalog
AS $$
    SELECT EXISTS (SELECT 1 FROM public.profiles p
        WHERE p.id = equipment_api_security.current_profile_id()
          AND p.is_active AND p.roles && allowed_roles)
$$;
REVOKE ALL ON FUNCTION equipment_api_security.has_any_role(text[]) FROM PUBLIC, anon, authenticated;

GRANT CONNECT ON DATABASE postgres TO equipment_api;
GRANT USAGE ON SCHEMA public, equipment_api_security TO equipment_api;
GRANT EXECUTE ON FUNCTION equipment_api_security.current_profile_id(),
    equipment_api_security.has_any_role(text[]), public.trim_whitespace(text) TO equipment_api;
GRANT SELECT (id, roles, is_active) ON public.profiles TO equipment_api;
GRANT SELECT ON public.equipment TO equipment_api;
GRANT INSERT (name, inventory_number, location, status) ON public.equipment TO equipment_api;
GRANT UPDATE (name, inventory_number, location, status) ON public.equipment TO equipment_api;
GRANT DELETE ON public.equipment TO equipment_api;
GRANT USAGE ON SEQUENCE public.equipment_id_seq TO equipment_api;

CREATE POLICY em_api_profiles_self ON public.profiles FOR SELECT TO equipment_api
USING (id = equipment_api_security.current_profile_id() AND is_active
    AND roles && ARRAY['engineer', 'maintenance_specialist', 'manager']::text[]);

CREATE POLICY em_api_equipment_read ON public.equipment FOR SELECT TO equipment_api
USING (equipment_api_security.has_any_role(ARRAY['engineer', 'maintenance_specialist', 'manager']::text[]));

CREATE POLICY em_api_equipment_insert ON public.equipment FOR INSERT TO equipment_api
WITH CHECK (equipment_api_security.has_any_role(ARRAY['engineer']::text[]));

CREATE POLICY em_api_equipment_update ON public.equipment FOR UPDATE TO equipment_api
USING (equipment_api_security.has_any_role(ARRAY['engineer']::text[]))
WITH CHECK (equipment_api_security.has_any_role(ARRAY['engineer']::text[]));

CREATE POLICY em_api_equipment_delete ON public.equipment FOR DELETE TO equipment_api
USING (equipment_api_security.has_any_role(ARRAY['engineer']::text[]));

-- Контроль эффективных прав, включая PUBLIC и column grants. При отклонении
-- откатывается вся миграция; права PUBLIC и equipment_app не исправляются молча.
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_catalog.pg_auth_members
        WHERE member = 'equipment_api'::regrole
           OR (roleid = 'equipment_api'::regrole
               AND NOT (member = current_user::regrole AND admin_option
                   AND NOT inherit_option AND NOT set_option))) THEN
        RAISE EXCEPTION 'Неожиданное членство equipment_api';
    END IF;
    -- PostgreSQL 17 может автоматически дать создателю ADMIN TRUE, SET FALSE,
    -- INHERIT FALSE. Это административная связь, а не права equipment_api
    -- на другую роль; допускается только для текущего доверенного создателя.
    IF EXISTS (SELECT 1 FROM pg_catalog.pg_namespace n
        WHERE n.nspname NOT IN ('pg_catalog', 'information_schema') AND n.nspname !~ '^pg_'
          AND has_schema_privilege('equipment_api', n.oid, 'CREATE')) THEN
        RAISE EXCEPTION 'Обнаружен CREATE в пользовательской схеме через PUBLIC';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_catalog.pg_attribute a
        WHERE a.attrelid IN ('public.profiles'::regclass, 'public.equipment'::regclass)
          AND a.attnum > 0 AND NOT a.attisdropped
          AND (has_column_privilege('equipment_api', a.attrelid, a.attnum, 'SELECT')
                IS DISTINCT FROM (a.attrelid = 'public.equipment'::regclass
                    OR a.attname IN ('id', 'roles', 'is_active'))
            OR has_column_privilege('equipment_api', a.attrelid, a.attnum, 'INSERT')
                IS DISTINCT FROM (a.attrelid = 'public.equipment'::regclass
                    AND a.attname IN ('name', 'inventory_number', 'location', 'status'))
            OR has_column_privilege('equipment_api', a.attrelid, a.attnum, 'UPDATE')
                IS DISTINCT FROM (a.attrelid = 'public.equipment'::regclass
                    AND a.attname IN ('name', 'inventory_number', 'location', 'status'))
            OR has_column_privilege('equipment_api', a.attrelid, a.attnum, 'REFERENCES'))) THEN
        RAISE EXCEPTION 'Лишние или отсутствующие права столбцов equipment/profiles';
    END IF;
    IF has_table_privilege('equipment_api', 'public.profiles',
            'INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
        OR has_table_privilege('equipment_api', 'public.equipment',
            'INSERT,UPDATE,TRUNCATE,REFERENCES,TRIGGER')
        OR NOT has_table_privilege('equipment_api', 'public.equipment', 'DELETE') THEN
        RAISE EXCEPTION 'Недопустимые табличные права equipment/profiles';
    END IF;
    -- MAINTAIN появился в PostgreSQL 17; проверяем отдельно для целевого Supabase 17.
    IF current_setting('server_version_num')::integer >= 170000 THEN
        IF EXISTS (SELECT 1 FROM pg_catalog.pg_class c
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relkind IN ('r', 'p', 'm')
              AND n.nspname NOT IN ('pg_catalog', 'information_schema') AND n.nspname !~ '^pg_'
              AND has_schema_privilege('equipment_api', n.oid, 'USAGE')
              AND has_table_privilege('equipment_api', c.oid, 'MAINTAIN')) THEN
            RAISE EXCEPTION 'Обнаружено лишнее право MAINTAIN';
        END IF;
    END IF;
    IF EXISTS (
        SELECT 1 FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
        WHERE c.relkind IN ('r', 'p', 'v', 'm', 'f')
          AND n.nspname NOT IN ('pg_catalog', 'information_schema') AND n.nspname !~ '^pg_'
          AND c.oid NOT IN ('public.profiles'::regclass, 'public.equipment'::regclass)
          AND has_schema_privilege('equipment_api', n.oid, 'USAGE')
          AND (has_table_privilege('equipment_api', c.oid,
                'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
            OR has_any_column_privilege('equipment_api', c.oid, 'SELECT,INSERT,UPDATE,REFERENCES'))
          -- Только штатные read-only представления расширения, как в create_app_role.sql.
          AND NOT (n.nspname = 'extensions' AND c.relkind = 'v'
            AND c.relname IN ('pg_stat_statements', 'pg_stat_statements_info')
            AND EXISTS (SELECT 1 FROM pg_catalog.pg_depend d
                JOIN pg_catalog.pg_extension e ON e.oid = d.refobjid
                WHERE d.classid = 'pg_catalog.pg_class'::regclass AND d.objid = c.oid
                  AND d.objsubid = 0 AND d.refclassid = 'pg_catalog.pg_extension'::regclass
                  AND d.deptype = 'e' AND e.extname = 'pg_stat_statements')
            AND NOT has_table_privilege('equipment_api', c.oid,
                'INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
            AND NOT has_any_column_privilege('equipment_api', c.oid, 'INSERT,UPDATE,REFERENCES'))
    ) THEN
        RAISE EXCEPTION 'Обнаружен неожиданный доступ к посторонним объектам';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
        WHERE c.relkind = 'S' AND n.nspname NOT IN ('pg_catalog', 'information_schema')
          AND n.nspname !~ '^pg_' AND has_schema_privilege('equipment_api', n.oid, 'USAGE')
          AND ((c.oid = 'public.equipment_id_seq'::regclass
                AND (NOT has_sequence_privilege('equipment_api', c.oid, 'USAGE')
                    OR has_sequence_privilege('equipment_api', c.oid, 'SELECT,UPDATE')))
            OR (c.oid <> 'public.equipment_id_seq'::regclass
                AND has_sequence_privilege('equipment_api', c.oid, 'USAGE,SELECT,UPDATE')))) THEN
        RAISE EXCEPTION 'Недопустимые права последовательностей';
    END IF;
    -- Единственное согласованное исключение: изученная инфраструктурная функция.
    -- PUBLIC EXECUTE сохраняется; новых GRANT/REVOKE здесь нет.
    -- Любое несовпадение метаданных/определения возвращает строгий отказ.
    IF EXISTS (SELECT 1 FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace
        WHERE p.prosecdef AND n.nspname NOT IN ('pg_catalog', 'information_schema')
          AND has_schema_privilege('equipment_api', n.oid, 'USAGE')
          AND has_function_privilege('equipment_api', p.oid, 'EXECUTE')
          AND NOT coalesce((
              p.oid = pg_catalog.to_regprocedure('public.rls_auto_enable()')
              AND n.nspname = 'public' AND p.proname = 'rls_auto_enable'
              AND p.prokind = 'f' AND p.pronargs = 0
              AND p.prorettype = 'pg_catalog.event_trigger'::regtype
              AND p.proowner = 'postgres'::regrole
              AND p.prolang = (SELECT oid FROM pg_catalog.pg_language WHERE lanname = 'plpgsql')
              AND p.proconfig = ARRAY['search_path=pg_catalog']::text[]
              AND pg_catalog.md5(pg_catalog.pg_get_functiondef(p.oid))
                  = '846ec2fbff751d24b890656d63c3a9bf'
              AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_depend d
                  WHERE d.classid = 'pg_catalog.pg_proc'::regclass AND d.objid = p.oid
                    AND d.refclassid = 'pg_catalog.pg_extension'::regclass AND d.deptype = 'e')
              AND EXISTS (SELECT 1 FROM pg_catalog.pg_event_trigger e
                  WHERE e.evtfoid = p.oid AND e.evtname = 'ensure_rls'
                    AND e.evtowner = 'postgres'::regrole
                    AND e.evtevent = 'ddl_command_end' AND e.evtenabled = 'O'
                    AND cardinality(e.evttags) = 3
                    AND e.evttags @> ARRAY['CREATE TABLE', 'CREATE TABLE AS', 'SELECT INTO']::text[]
                    AND e.evttags <@ ARRAY['CREATE TABLE', 'CREATE TABLE AS', 'SELECT INTO']::text[])
              AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_event_trigger e
                  WHERE e.evtfoid = p.oid AND e.evtname <> 'ensure_rls')
          ), false)) THEN
        RAISE EXCEPTION 'Доступна посторонняя SECURITY DEFINER функция: требуется аудит';
    END IF;
END;
$$;

COMMENT ON ROLE equipment_api IS 'Серверный CRUD оборудования; UUID контекста задаёт только API после проверки JWT';
COMMENT ON SCHEMA equipment_api_security IS 'Не публиковать в Data API; SECURITY INVOKER helpers для серверного RLS';
COMMIT;
