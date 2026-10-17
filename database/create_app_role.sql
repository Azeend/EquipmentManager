-- Проект однократного создания серверного читателя профилей EquipmentManager.
-- НЕ ВЫПОЛНЯЛСЯ. Выполнять вручную администратором только после подтверждения.
-- Пароля, service_role, отключения RLS и доступа ко всем таблицам здесь нет.
BEGIN;

DO $$
BEGIN
    IF current_database() <> 'postgres' THEN
        RAISE EXCEPTION 'Ожидается база postgres; проверьте выбранный проект';
    END IF;
    IF to_regclass('public.profiles') IS NULL THEN
        RAISE EXCEPTION 'Сначала требуется таблица public.profiles из schema.sql';
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM pg_catalog.pg_class
        WHERE oid = 'public.profiles'::regclass AND relrowsecurity
    ) THEN
        RAISE EXCEPTION 'RLS profiles должен быть включён до создания роли';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = 'equipment_app') THEN
        RAISE EXCEPTION 'Роль equipment_app уже существует: проверьте её отдельно, повторная установка запрещена';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_catalog.pg_policies WHERE schemaname = 'public'
        AND tablename = 'profiles' AND policyname = 'em_profiles_equipment_app_read') THEN
        RAISE EXCEPTION 'Политика em_profiles_equipment_app_read уже существует';
    END IF;
END;
$$;

-- Без PASSWORD: пароль отсутствует, парольный вход невозможен до ручной настройки.
CREATE ROLE equipment_app LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE
    NOINHERIT NOREPLICATION NOBYPASSRLS;

GRANT CONNECT ON DATABASE postgres TO equipment_app;
GRANT USAGE ON SCHEMA public TO equipment_app;
GRANT SELECT (id, roles, is_active) ON public.profiles TO equipment_app;

-- Только отдельный доверенный database login, не PUBLIC/anon/authenticated.
-- Не используется auth.uid(): обычное Npgsql-подключение не имеет JWT-контекста.
-- API выбирает профиль параметром WHERE id = $1 по sub проверенного JWT.
CREATE POLICY em_profiles_equipment_app_read ON public.profiles
    FOR SELECT TO equipment_app USING (true);

-- PUBLIC-привилегии действуют и при NOINHERIT. Не меняем глобальные grants,
-- а откатываем установку, если окружение даёт роли дополнительные права.
DO $$
BEGIN
    IF has_schema_privilege('equipment_app', 'public', 'CREATE') THEN
        RAISE EXCEPTION 'Через PUBLIC доступен CREATE в public: требуется отдельный аудит прав';
    END IF;
    IF EXISTS (
        SELECT 1 FROM pg_catalog.pg_namespace n
        WHERE n.nspname NOT IN ('pg_catalog', 'information_schema')
          AND n.nspname !~ '^pg_'
          AND has_schema_privilege('equipment_app', n.oid, 'CREATE')
    ) THEN
        RAISE EXCEPTION 'Доступен CREATE в пользовательской схеме: требуется отдельный аудит PUBLIC';
    END IF;
    IF EXISTS (
        SELECT 1 FROM pg_catalog.pg_class c
        JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
        WHERE c.relkind IN ('r', 'p', 'v', 'm', 'f')
          AND n.nspname NOT IN ('pg_catalog', 'information_schema') AND n.nspname !~ '^pg_'
          AND c.oid <> 'public.profiles'::regclass
          -- Для обращения к объекту нужны и права на него, и USAGE его схемы.
          -- Не выдаём USAGE сами: учитываем только уже действующий доступ.
          AND has_schema_privilege('equipment_app', n.oid, 'USAGE')
          AND (has_table_privilege('equipment_app', c.oid,
              'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
            OR has_any_column_privilege('equipment_app', c.oid, 'SELECT,INSERT,UPDATE,REFERENCES'))
          -- Узкое исключение: только два штатных read-only представления
          -- расширения pg_stat_statements в extensions, не вся схема/расширение.
          AND NOT (
              n.nspname = 'extensions' AND c.relkind = 'v'
              AND c.relname IN ('pg_stat_statements', 'pg_stat_statements_info')
              AND EXISTS (
                  SELECT 1 FROM pg_catalog.pg_depend d
                  JOIN pg_catalog.pg_extension e ON e.oid = d.refobjid
                  WHERE d.classid = 'pg_catalog.pg_class'::regclass
                    AND d.objid = c.oid AND d.objsubid = 0
                    AND d.refclassid = 'pg_catalog.pg_extension'::regclass
                    AND d.deptype = 'e' AND e.extname = 'pg_stat_statements'
              )
              AND NOT has_table_privilege('equipment_app', c.oid,
                  'INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
              AND NOT has_any_column_privilege('equipment_app', c.oid,
                  'INSERT,UPDATE,REFERENCES')
          )
    ) THEN
        RAISE EXCEPTION 'Обнаружен доступ к посторонним таблицам через PUBLIC; установка откатывается';
    END IF;
    IF has_table_privilege('equipment_app', 'public.profiles',
        'INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
        OR has_any_column_privilege('equipment_app', 'public.profiles', 'INSERT,UPDATE,REFERENCES')
        OR EXISTS (
            SELECT 1 FROM pg_catalog.pg_attribute a
            WHERE a.attrelid = 'public.profiles'::regclass AND a.attnum > 0 AND NOT a.attisdropped
              AND a.attname NOT IN ('id', 'roles', 'is_active')
              AND has_column_privilege('equipment_app', a.attrelid, a.attnum, 'SELECT')
        ) THEN
        RAISE EXCEPTION 'PUBLIC выдаёт лишние права profiles; установка откатывается';
    END IF;
END;
$$;

COMMENT ON ROLE equipment_app IS 'Серверный читатель profiles(id, roles, is_active) для проверки полномочий API; не бизнес-роль';
COMMIT;
