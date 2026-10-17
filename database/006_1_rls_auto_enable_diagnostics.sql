-- Только SELECT; ничего не вызывает и не меняет. Запускать вручную владельцем.
-- Определение выводится намеренно для локального аудита; не публиковать без просмотра.
SELECT current_database(), current_user, version(),
       current_setting('session_replication_role') AS replication_role,
       current_setting('event_triggers', true) AS event_triggers;

SELECT p.oid, p.oid::regprocedure AS function_identity,
       pg_catalog.pg_get_userbyid(p.proowner) AS owner_name,
       l.lanname, p.prokind, p.prorettype::regtype AS return_type,
       p.pronargs, p.prosecdef, p.proconfig, p.proacl,
       pg_catalog.md5(pg_catalog.pg_get_functiondef(p.oid)) AS definition_md5,
       pg_catalog.pg_get_functiondef(p.oid) AS definition
FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_language l ON l.oid = p.prolang
WHERE p.oid = pg_catalog.to_regprocedure('public.rls_auto_enable()');

-- Проверяем фактическую привязку по OID, а не предположение по имени ensure_rls.
SELECT e.oid, e.evtname, e.evtevent, e.evtenabled, e.evttags,
       pg_catalog.pg_get_userbyid(e.evtowner) AS event_owner,
       e.evtfoid::regprocedure AS function_identity
FROM pg_catalog.pg_event_trigger e
WHERE e.evtfoid = pg_catalog.to_regprocedure('public.rls_auto_enable()');

-- Развёрнутые ACL: grantee=PUBLIC может отсутствовать при явном REVOKE.
SELECT CASE WHEN a.grantee = 0 THEN 'PUBLIC'
            ELSE pg_catalog.pg_get_userbyid(a.grantee) END AS grantee,
       pg_catalog.pg_get_userbyid(a.grantor) AS grantor,
       a.privilege_type, a.is_grantable
FROM pg_catalog.pg_proc p
CROSS JOIN LATERAL pg_catalog.aclexplode(
    coalesce(p.proacl, pg_catalog.acldefault('f', p.proowner))) a
WHERE p.oid = pg_catalog.to_regprocedure('public.rls_auto_enable()');

-- Реальные эффективные права существующих ролей (PUBLIC/членство/владелец).
SELECT r.rolname, r.rolsuper, r.rolbypassrls,
       pg_catalog.has_schema_privilege(r.oid, p.pronamespace, 'USAGE') AS schema_usage,
       pg_catalog.has_function_privilege(r.oid, p.oid, 'EXECUTE') AS effective_execute,
       pg_catalog.pg_has_role(r.oid, p.proowner, 'USAGE') AS inherits_function_owner
FROM pg_catalog.pg_roles r CROSS JOIN pg_catalog.pg_proc p
WHERE p.oid = pg_catalog.to_regprocedure('public.rls_auto_enable()')
  AND r.rolname IN ('postgres', 'supabase_admin', 'supabase_auth_admin',
                   'authenticator', 'anon', 'authenticated', 'service_role',
                   'equipment_app', 'equipment_api')
ORDER BY r.rolname;

-- Зависимости в обе стороны: extension membership, язык, схема, event triggers и т.д.
SELECT 'function depends on' AS direction, d.deptype,
       pg_catalog.pg_describe_object(d.refclassid, d.refobjid, d.refobjsubid) AS object_identity
FROM pg_catalog.pg_depend d
WHERE d.classid = 'pg_catalog.pg_proc'::regclass
  AND d.objid = pg_catalog.to_regprocedure('public.rls_auto_enable()')
UNION ALL
SELECT 'object depends on function', d.deptype,
       pg_catalog.pg_describe_object(d.classid, d.objid, d.objsubid)
FROM pg_catalog.pg_depend d
WHERE d.refclassid = 'pg_catalog.pg_proc'::regclass
  AND d.refobjid = pg_catalog.to_regprocedure('public.rls_auto_enable()');

-- Владение и ACL-зависимости на роли хранятся отдельно.
SELECT d.deptype, r.rolname AS referenced_role
FROM pg_catalog.pg_shdepend d JOIN pg_catalog.pg_roles r ON r.oid = d.refobjid
WHERE d.dbid = (SELECT oid FROM pg_catalog.pg_database WHERE datname = current_database())
  AND d.classid = 'pg_catalog.pg_proc'::regclass
  AND d.objid = pg_catalog.to_regprocedure('public.rls_auto_enable()')
  AND d.refclassid = 'pg_catalog.pg_authid'::regclass;

-- Эвристика текстовых обращений; не доказывает отсутствия внешних/динамических вызовов.
SELECT p.oid::regprocedure AS possible_text_reference
FROM pg_catalog.pg_proc p
WHERE p.oid <> pg_catalog.to_regprocedure('public.rls_auto_enable()')
  AND p.prosrc ILIKE '%rls_auto_enable%';

-- Уже существующие таблицы: auto-RLS не является проверкой всей схемы задним числом.
SELECT c.oid::regclass AS table_identity, c.relrowsecurity, c.relforcerowsecurity
FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p')
ORDER BY c.relname;
