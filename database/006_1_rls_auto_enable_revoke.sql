-- ИСТОРИЧЕСКИЙ ОТКЛОНЁННЫЙ ВАРИАНТ. По решению пользователя от 17.10.2026 НЕ ВЫПОЛНЯТЬ.
-- Используется адресное исключение в 006_1, инфраструктурные ACL сохраняются.
-- Прежний кандидат исправления ПЕРЕД 006_1_equipment_api_security.sql. НЕ ВЫПОЛНЯЛСЯ.
-- Только после ручного аудита diagnostics и проверки на отдельном стенде.
-- Намеренно закрыт до подстановки MD5 ПРОСМОТРЕННОГО определения функции.
-- MD5 здесь — контроль неизменности определения, не криптографическое доверие.
BEGIN;
DO $audit$
DECLARE
    reviewedDefinitionMd5 constant text := NULL; -- Заменить на строку из diagnostics ПОСЛЕ просмотра тела.
    functionOid oid := pg_catalog.to_regprocedure('public.rls_auto_enable()');
BEGIN
    IF reviewedDefinitionMd5 IS NULL OR reviewedDefinitionMd5 !~ '^[0-9a-f]{32}$' THEN
        RAISE EXCEPTION 'Сначала изучите тело, зависимости и ACL; зафиксируйте reviewedDefinitionMd5';
    END IF;
    IF current_database() <> 'postgres' OR current_user <> 'postgres'
        OR current_setting('server_version_num')::integer < 170000 THEN
        RAISE EXCEPTION 'Ожидается postgres в базе postgres, PostgreSQL 17+';
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM pg_catalog.pg_proc p
        JOIN pg_catalog.pg_language l ON l.oid = p.prolang
        WHERE p.oid = functionOid AND p.prokind = 'f' AND p.pronargs = 0
          AND p.prorettype = 'pg_catalog.event_trigger'::regtype
          AND p.proowner = 'postgres'::regrole AND p.prosecdef AND l.lanname = 'plpgsql'
          AND p.proconfig @> ARRAY['search_path=pg_catalog']
          AND pg_catalog.md5(pg_catalog.pg_get_functiondef(p.oid)) = reviewedDefinitionMd5
    ) THEN
        RAISE EXCEPTION 'Определение/тип/владелец rls_auto_enable не соответствуют изученному объекту';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_catalog.pg_depend d
        WHERE d.classid = 'pg_catalog.pg_proc'::regclass AND d.objid = functionOid
          AND d.refclassid = 'pg_catalog.pg_extension'::regclass AND d.deptype = 'e') THEN
        RAISE EXCEPTION 'Функция управляется расширением: нужен отдельный разбор';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_event_trigger e
        WHERE e.evtfoid = functionOid AND e.evtname = 'ensure_rls'
          AND e.evtowner = 'postgres'::regrole AND e.evtevent = 'ddl_command_end'
          AND e.evtenabled IN ('O', 'A')
          AND e.evttags @> ARRAY['CREATE TABLE', 'CREATE TABLE AS', 'SELECT INTO']::text[]
          AND e.evttags <@ ARRAY['CREATE TABLE', 'CREATE TABLE AS', 'SELECT INTO']::text[])
       OR EXISTS (SELECT 1 FROM pg_catalog.pg_event_trigger e
           WHERE e.evtfoid = functionOid AND e.evtname <> 'ensure_rls') THEN
        RAISE EXCEPTION 'Ожидается только изученный включённый ensure_rls с тремя CREATE-тегами';
    END IF;
END;
$audit$;

-- Не меняет тело, владельца, event trigger, роли, RLS, прочие ACL или default privileges.
REVOKE EXECUTE ON FUNCTION public.rls_auto_enable() FROM PUBLIC;

DO $verify$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_catalog.pg_proc p
        CROSS JOIN LATERAL pg_catalog.aclexplode(
            coalesce(p.proacl, pg_catalog.acldefault('f', p.proowner))) a
        WHERE p.oid = 'public.rls_auto_enable()'::regprocedure
          AND a.grantee = 0 AND a.privilege_type = 'EXECUTE') THEN
        RAISE EXCEPTION 'PUBLIC EXECUTE не отозван';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_catalog.pg_roles r
        WHERE r.rolname = 'equipment_api'
          AND pg_catalog.has_function_privilege(r.oid,
              'public.rls_auto_enable()'::regprocedure, 'EXECUTE')) THEN
        RAISE EXCEPTION 'equipment_api всё ещё получает EXECUTE другим путём; требуется аудит ACL';
    END IF;
END;
$verify$;
COMMIT;
