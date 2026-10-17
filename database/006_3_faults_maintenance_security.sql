-- Этап 6.3: дополнительная однократная миграция ПОСЛЕ проверенной 006_1.
-- Только подготовлена. Не выполнялась в Supabase. 006_1 не изменяется.
-- Не создаёт роли/таблицы/функции, не меняет существующие триггеры и профили.
BEGIN;

DO $$
BEGIN
    IF current_database() <> 'postgres' OR current_setting('server_version_num')::integer < 170000 THEN
        RAISE EXCEPTION 'Требуется целевая база postgres на PostgreSQL 17+';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles
        WHERE rolname = 'equipment_api' AND rolcanlogin
          AND NOT (rolsuper OR rolbypassrls OR rolcreatedb OR rolcreaterole OR rolreplication OR rolinherit))
       OR EXISTS (SELECT 1 FROM pg_catalog.pg_auth_members WHERE member = 'equipment_api'::regrole)
       OR to_regprocedure('equipment_api_security.current_profile_id()') IS NULL
       OR to_regprocedure('equipment_api_security.has_any_role(text[])') IS NULL THEN
        RAISE EXCEPTION 'Сначала требуется успешно установленная 006_1 с ограниченной equipment_api';
    END IF;
    IF (SELECT count(*) FROM pg_catalog.pg_policy
        WHERE polrelid IN ('public.profiles'::regclass, 'public.equipment'::regclass)
          AND polname IN ('em_api_profiles_self', 'em_api_equipment_read', 'em_api_equipment_insert',
              'em_api_equipment_update', 'em_api_equipment_delete')
          AND polroles = ARRAY['equipment_api'::regrole::oid]) <> 5 THEN
        RAISE EXCEPTION 'Не подтверждены пять исходных политик 006_1';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_catalog.pg_class
        WHERE oid IN ('public.profiles'::regclass, 'public.equipment'::regclass,
            'public.faults'::regclass, 'public.maintenance_records'::regclass)
          AND (NOT relrowsecurity OR relkind <> 'r' OR relowner = 'equipment_api'::regrole))
       OR has_schema_privilege('equipment_api', 'public', 'CREATE') THEN
        RAISE EXCEPTION 'Требуются исходные таблицы с RLS без владения/CREATE у equipment_api';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_catalog.pg_policy
        WHERE polrelid IN ('public.faults'::regclass, 'public.maintenance_records'::regclass)
          AND (0::oid = ANY(polroles) OR 'equipment_api'::regrole::oid = ANY(polroles))) THEN
        RAISE EXCEPTION 'PUBLIC/equipment_api политики уже существуют: повторная установка или конфликт';
    END IF;
    IF (SELECT count(*) FROM pg_catalog.pg_trigger
        WHERE NOT tgisinternal AND tgrelid IN ('public.faults'::regclass, 'public.maintenance_records'::regclass)) <> 3
       OR (SELECT count(*) FROM pg_catalog.pg_trigger
        WHERE NOT tgisinternal AND tgenabled = 'O'
          AND ((tgrelid = 'public.faults'::regclass AND tgname = 'faults_guard'
                AND tgfoid = 'public.guard_fault()'::regprocedure)
            OR (tgrelid = 'public.faults'::regclass AND tgname = 'faults_create_history'
                AND tgfoid = 'public.create_maintenance_record()'::regprocedure)
            OR (tgrelid = 'public.maintenance_records'::regclass AND tgname = 'maintenance_records_guard'
                AND tgfoid = 'public.guard_maintenance_record()'::regprocedure))) <> 3
       OR EXISTS (SELECT 1 FROM pg_catalog.pg_proc
        WHERE oid IN ('public.guard_fault()'::regprocedure, 'public.create_maintenance_record()'::regprocedure,
            'public.guard_maintenance_record()'::regprocedure)
          AND (prosecdef OR NOT coalesce(proconfig @> ARRAY['search_path=pg_catalog'], false))) THEN
        RAISE EXCEPTION 'Требуются три исходных включённых INVOKER-триггера';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_constraint
        WHERE conname = 'maintenance_fault_equipment_fk' AND convalidated AND contype = 'f'
          AND conrelid = 'public.maintenance_records'::regclass AND confrelid = 'public.faults'::regclass
          AND confdeltype = 'r') THEN
        RAISE EXCEPTION 'Требуется исходная связь истории с заявкой и оборудованием';
    END IF;
END;
$$;

GRANT SELECT ON public.faults, public.maintenance_records TO equipment_api;
GRANT INSERT (equipment_id, description, created_by) ON public.faults TO equipment_api;
GRANT UPDATE (status, started_by, completed_by, work_description, result) ON public.faults TO equipment_api;
-- Нужен существующему INVOKER AFTER-триггеру, не прямому INSERT из C#.
GRANT INSERT (fault_id, equipment_id, equipment_label, performed_at, work_description, result, performed_by)
    ON public.maintenance_records TO equipment_api;
GRANT USAGE ON SEQUENCE public.faults_id_seq TO equipment_api;
-- EXECUTE триггерных функций не выдаётся: существующие триггеры не требуют его у DML-пользователя.
-- Прав на display_name чужих профилей и записи профилей не добавляется.

CREATE POLICY em_api_faults_read ON public.faults FOR SELECT TO equipment_api
USING (equipment_api_security.has_any_role(ARRAY['engineer', 'maintenance_specialist', 'manager']::text[]));

CREATE POLICY em_api_faults_insert ON public.faults FOR INSERT TO equipment_api
WITH CHECK (created_by = equipment_api_security.current_profile_id() AND status = 'Новая'
    AND equipment_api_security.has_any_role(ARRAY['engineer', 'maintenance_specialist']::text[]));

CREATE POLICY em_api_faults_update ON public.faults FOR UPDATE TO equipment_api
USING (status IN ('Новая', 'В работе')
    AND equipment_api_security.has_any_role(ARRAY['maintenance_specialist']::text[]))
WITH CHECK (equipment_api_security.has_any_role(ARRAY['maintenance_specialist']::text[])
    AND ((status = 'В работе' AND started_by = equipment_api_security.current_profile_id())
      OR (status = 'Завершена' AND completed_by = equipment_api_security.current_profile_id())));

CREATE POLICY em_api_history_read ON public.maintenance_records FOR SELECT TO equipment_api
USING (equipment_api_security.has_any_role(ARRAY['engineer', 'maintenance_specialist', 'manager']::text[]));

-- Глубина — дополнительная защита; личность/роль, состояние и полный снимок проверяются отдельно.
CREATE POLICY em_api_history_from_completion ON public.maintenance_records FOR INSERT TO equipment_api
WITH CHECK (pg_catalog.pg_trigger_depth() = 1
    AND performed_by = equipment_api_security.current_profile_id()
    AND equipment_api_security.has_any_role(ARRAY['maintenance_specialist']::text[])
    AND EXISTS (SELECT 1 FROM public.faults f
        WHERE f.id = maintenance_records.fault_id AND f.status = 'Завершена'
          AND f.completed_by = equipment_api_security.current_profile_id()
          AND ROW(f.equipment_id, f.equipment_label, f.completed_at, f.work_description, f.result, f.completed_by)
              IS NOT DISTINCT FROM ROW(maintenance_records.equipment_id, maintenance_records.equipment_label,
                  maintenance_records.performed_at, maintenance_records.work_description,
                  maintenance_records.result, maintenance_records.performed_by)));

-- Проверяем эффективные права, включая PUBLIC; глобальные grants молча не отзываются.
DO $$
BEGIN
    IF has_table_privilege('equipment_api', 'public.faults', 'INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER,MAINTAIN')
       OR has_table_privilege('equipment_api', 'public.maintenance_records', 'INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER,MAINTAIN')
       OR NOT has_table_privilege('equipment_api', 'public.faults', 'SELECT')
       OR NOT has_table_privilege('equipment_api', 'public.maintenance_records', 'SELECT') THEN
        RAISE EXCEPTION 'Лишние либо отсутствующие табличные права: ожидаются SELECT и только column grants записи';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_catalog.pg_attribute a
        WHERE a.attrelid IN ('public.faults'::regclass, 'public.maintenance_records'::regclass)
          AND a.attnum > 0 AND NOT a.attisdropped
          AND (has_column_privilege('equipment_api', a.attrelid, a.attnum, 'INSERT') IS DISTINCT FROM
                (CASE WHEN a.attrelid = 'public.faults'::regclass
                    THEN a.attname IN ('equipment_id', 'description', 'created_by')
                    ELSE a.attname IN ('fault_id', 'equipment_id', 'equipment_label', 'performed_at', 'work_description', 'result', 'performed_by') END)
            OR has_column_privilege('equipment_api', a.attrelid, a.attnum, 'UPDATE') IS DISTINCT FROM
                (a.attrelid = 'public.faults'::regclass AND a.attname IN ('status', 'started_by', 'completed_by', 'work_description', 'result'))
            OR has_column_privilege('equipment_api', a.attrelid, a.attnum, 'REFERENCES'))) THEN
        RAISE EXCEPTION 'Неожиданные права столбцов заявок/истории';
    END IF;
    IF NOT has_sequence_privilege('equipment_api', 'public.faults_id_seq', 'USAGE')
        OR has_sequence_privilege('equipment_api', 'public.faults_id_seq', 'SELECT,UPDATE') THEN
        RAISE EXCEPTION 'Ожидается только USAGE последовательности заявок';
    END IF;
END;
$$;
COMMIT;
