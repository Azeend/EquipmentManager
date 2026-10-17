-- Только SELECT. Выполнять вручную до миграции, роль equipment_api ещё не нужна.
-- Не вызывает проверяемые функции, не выводит их тела, пароли или JWT.

SELECT current_database() AS database_name, current_user AS migration_executor,
       current_setting('server_version_num')::integer AS server_version_num,
       r.rolsuper, r.rolcreaterole,
       current_setting('createrole_self_grant', true) AS createrole_self_grant,
       has_database_privilege(current_user, current_database(), 'CREATE') AS can_create_schema,
       has_database_privilege(current_user, current_database(), 'CONNECT') AS can_connect
FROM pg_catalog.pg_roles r WHERE r.rolname = current_user;

-- До создания роли моделируется только PUBLIC + будущий USAGE public.
-- Прямых grants и членства у ещё не существующего login нет.
-- После создания роли используются её фактические эффективные права.
WITH target AS (
    SELECT (SELECT oid FROM pg_catalog.pg_roles WHERE rolname = 'equipment_api') AS role_oid
), candidates AS (
    SELECT p.oid, n.nspname, p.proname,
           pg_catalog.pg_get_function_identity_arguments(p.oid) AS identity_arguments,
           p.prokind, pg_catalog.pg_get_userbyid(p.proowner) AS owner_name,
           EXISTS (SELECT 1 FROM pg_catalog.aclexplode(
               coalesce(n.nspacl, pg_catalog.acldefault('n', n.nspowner))) a
               WHERE a.grantee = 0 AND a.privilege_type = 'USAGE') AS public_schema_usage,
           EXISTS (SELECT 1 FROM pg_catalog.aclexplode(
               coalesce(p.proacl, pg_catalog.acldefault('f', p.proowner))) a
               WHERE a.grantee = 0 AND a.privilege_type = 'EXECUTE') AS public_execute,
           CASE WHEN t.role_oid IS NULL THEN n.nspname = 'public' OR EXISTS (
               SELECT 1 FROM pg_catalog.aclexplode(
                   coalesce(n.nspacl, pg_catalog.acldefault('n', n.nspowner))) a
               WHERE a.grantee = 0 AND a.privilege_type = 'USAGE')
             ELSE pg_catalog.has_schema_privilege(t.role_oid, n.oid, 'USAGE') END AS effective_schema_usage,
           CASE WHEN t.role_oid IS NULL THEN EXISTS (
               SELECT 1 FROM pg_catalog.aclexplode(
                   coalesce(p.proacl, pg_catalog.acldefault('f', p.proowner))) a
               WHERE a.grantee = 0 AND a.privilege_type = 'EXECUTE')
             ELSE pg_catalog.has_function_privilege(t.role_oid, p.oid, 'EXECUTE') END AS effective_execute,
           t.role_oid IS NOT NULL AS uses_existing_role_acl,
           e.extname AS extension_name, e.extversion AS extension_version
    FROM pg_catalog.pg_proc p
    JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace
    CROSS JOIN target t
    LEFT JOIN pg_catalog.pg_depend d
      ON d.classid = 'pg_catalog.pg_proc'::regclass AND d.objid = p.oid AND d.objsubid = 0
     AND d.refclassid = 'pg_catalog.pg_extension'::regclass AND d.deptype = 'e'
    LEFT JOIN pg_catalog.pg_extension e ON e.oid = d.refobjid
    WHERE p.prosecdef AND n.nspname NOT IN ('pg_catalog', 'information_schema')
)
SELECT oid, format('%I.%I(%s)', nspname, proname, identity_arguments) AS routine_identity,
       prokind, owner_name, extension_name, extension_version,
       public_schema_usage, public_execute, effective_schema_usage, effective_execute,
       uses_existing_role_acl,
       effective_schema_usage AND effective_execute AS would_block_migration
FROM candidates
WHERE effective_schema_usage AND effective_execute
ORDER BY nspname, proname, identity_arguments;

-- Наличие функции в результате — не автоматический allowlist:
-- даже штатная DEFINER-функция может расширять полномочия login.
-- Пустой результат не является полным аудитом RPC/расширений всей БД.

SELECT c.oid::regclass AS table_name, pg_catalog.pg_get_userbyid(c.relowner) AS owner_name,
       c.relrowsecurity, c.relforcerowsecurity,
       pg_catalog.pg_has_role(current_user, c.relowner, 'USAGE') AS executor_has_owner_role
FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
WHERE n.nspname = 'public'
  AND c.relname IN ('profiles', 'equipment', 'faults', 'maintenance_records')
ORDER BY c.relname;

SELECT parent.rolname AS granted_role, child.rolname AS member_role,
       m.admin_option, m.inherit_option, m.set_option,
       pg_catalog.pg_get_userbyid(m.grantor) AS grantor_name
FROM pg_catalog.pg_auth_members m
JOIN pg_catalog.pg_roles parent ON parent.oid = m.roleid
JOIN pg_catalog.pg_roles child ON child.oid = m.member
WHERE parent.rolname IN ('equipment_api', 'equipment_app')
   OR child.rolname IN ('equipment_api', 'equipment_app')
ORDER BY parent.rolname, child.rolname;

-- При отсутствии equipment_api возвращает 0 строк, а не ошибку неизвестной роли.
SELECT c.oid::regclass AS table_name,
       has_table_privilege(r.oid, c.oid, 'SELECT') AS table_select,
       has_table_privilege(r.oid, c.oid, 'INSERT') AS table_insert,
       has_any_column_privilege(r.oid, c.oid, 'INSERT') AS any_column_insert,
       has_table_privilege(r.oid, c.oid, 'UPDATE') AS table_update,
       has_any_column_privilege(r.oid, c.oid, 'UPDATE') AS any_column_update,
       has_table_privilege(r.oid, c.oid, 'DELETE') AS table_delete
FROM pg_catalog.pg_roles r
CROSS JOIN pg_catalog.pg_class c
JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
WHERE r.rolname = 'equipment_api' AND n.nspname = 'public'
  AND c.relname IN ('profiles', 'equipment', 'faults', 'maintenance_records')
ORDER BY c.relname;
