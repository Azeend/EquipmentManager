-- Лабораторная №5: ПРЕДЛОЖЕНИЕ для Supabase Auth + пользовательского JWT.
-- Применять только после выбора этого варианта и проверок security_description.md.
-- Не выполнялся. Однократная миграция поверх проверенного schema.sql.
-- Не предназначен для обычного подключения ASP.NET с общей login-ролью.
BEGIN;

DO $$
BEGIN
    IF to_regprocedure('auth.uid()') IS NULL
        OR to_regclass('public.profiles') IS NULL
        OR to_regclass('public.equipment') IS NULL
        OR to_regclass('public.faults') IS NULL
        OR to_regclass('public.maintenance_records') IS NULL THEN
        RAISE EXCEPTION 'Сначала требуется проверенная схема EquipmentManager и Supabase Auth';
    END IF;
    IF EXISTS (
        SELECT 1 FROM pg_catalog.pg_policies
        WHERE schemaname = 'public'
          AND tablename IN ('profiles', 'equipment', 'faults', 'maintenance_records')
    ) THEN
        RAISE EXCEPTION 'Уже существуют политики: требуется отдельная миграция, а не повторный запуск';
    END IF;
    IF EXISTS (
        SELECT 1 FROM pg_catalog.pg_roles
        WHERE rolname IN ('anon', 'authenticated') AND (rolsuper OR rolbypassrls)
    ) OR EXISTS (
        SELECT 1 FROM pg_catalog.pg_class c
        JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
        JOIN pg_catalog.pg_roles r ON r.oid = c.relowner
        WHERE n.nspname = 'public'
          AND c.relname IN ('profiles', 'equipment', 'faults', 'maintenance_records')
          AND r.rolname IN ('anon', 'authenticated')
    ) THEN
        RAISE EXCEPTION 'Клиентские роли не должны владеть таблицами или обходить RLS';
    END IF;
    -- Не скрываем ранее выданные column-level grants, которые REVOKE ON TABLE не снимает.
    IF EXISTS (
        SELECT 1 FROM pg_catalog.pg_attribute a
        JOIN pg_catalog.pg_class c ON c.oid = a.attrelid
        JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = 'public'
          AND c.relname IN ('profiles', 'equipment', 'faults', 'maintenance_records')
          AND a.attnum > 0 AND NOT a.attisdropped AND a.attacl IS NOT NULL
    ) THEN
        RAISE EXCEPTION 'Обнаружены отдельные права столбцов: сначала проверьте ACL';
    END IF;
    IF (SELECT count(*) FROM pg_catalog.pg_trigger t
        WHERE NOT t.tgisinternal AND t.tgenabled = 'O'
          AND ((t.tgrelid = 'public.faults'::regclass
                AND t.tgname IN ('faults_guard', 'faults_create_history'))
            OR (t.tgrelid = 'public.maintenance_records'::regclass
                AND t.tgname = 'maintenance_records_guard'))) <> 3 THEN
        RAISE EXCEPTION 'Требуются три включённых триггера исходной схемы';
    END IF;
    IF EXISTS (
        SELECT 1 FROM pg_catalog.pg_proc p
        WHERE p.oid IN ('public.guard_fault()'::regprocedure,
                        'public.create_maintenance_record()'::regprocedure,
                        'public.guard_maintenance_record()'::regprocedure)
          AND p.prosecdef
    ) THEN
        RAISE EXCEPTION 'Ожидаются SECURITY INVOKER триггеры';
    END IF;
END;
$$;

ALTER TABLE public.profiles ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.equipment ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.faults ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.maintenance_records ENABLE ROW LEVEL SECURITY;

REVOKE ALL ON TABLE public.profiles, public.equipment, public.faults,
    public.maintenance_records FROM PUBLIC, anon, authenticated;
REVOKE ALL ON SEQUENCE public.equipment_id_seq, public.faults_id_seq
    FROM PUBLIC, anon, authenticated;
REVOKE ALL ON FUNCTION public.trim_whitespace(text), public.guard_fault(),
    public.create_maintenance_record(), public.guard_maintenance_record()
    FROM PUBLIC, anon, authenticated;

GRANT USAGE ON SCHEMA public TO authenticated;
GRANT SELECT ON TABLE public.profiles, public.equipment, public.faults,
    public.maintenance_records TO authenticated;
GRANT INSERT (name, inventory_number, location, status) ON public.equipment TO authenticated;
GRANT INSERT (equipment_id, description, created_by) ON public.faults TO authenticated;
GRANT UPDATE (status, started_by, completed_by, work_description, result)
    ON public.faults TO authenticated;
-- INVOKER AFTER-триггер использует те же права, что и UPDATE заявки.
-- INSERT доступен только с RLS-проверкой ниже; UPDATE/DELETE истории не выдаются.
GRANT INSERT ON public.maintenance_records TO authenticated;
GRANT USAGE ON SEQUENCE public.equipment_id_seq, public.faults_id_seq TO authenticated;
-- CHECK / generated column вызывают эту чистую функцию с правами пользователя.
GRANT EXECUTE ON FUNCTION public.trim_whitespace(text) TO authenticated;
-- EXECUTE триггерных функций клиентам не выдаётся: триггеры уже созданы владельцем.

-- Нет рекурсии: политика profiles не обращается ни к profiles, ни к другим таблицам.
-- Ни INSERT, ни UPDATE, ни DELETE профиля клиенту не разрешены.
CREATE POLICY em_profiles_read_self ON public.profiles FOR SELECT TO authenticated
USING (id = (SELECT auth.uid()) AND is_active
    AND roles && ARRAY['engineer', 'maintenance_specialist', 'manager']::text[]);

CREATE POLICY em_equipment_read ON public.equipment FOR SELECT TO authenticated
USING (EXISTS (SELECT 1 FROM public.profiles p WHERE p.id = (SELECT auth.uid())
    AND p.is_active AND p.roles && ARRAY['engineer', 'maintenance_specialist', 'manager']::text[]));

CREATE POLICY em_equipment_create ON public.equipment FOR INSERT TO authenticated
WITH CHECK (EXISTS (SELECT 1 FROM public.profiles p WHERE p.id = (SELECT auth.uid())
    AND p.is_active AND 'engineer' = ANY(p.roles)));

CREATE POLICY em_faults_read ON public.faults FOR SELECT TO authenticated
USING (EXISTS (SELECT 1 FROM public.profiles p WHERE p.id = (SELECT auth.uid())
    AND p.is_active AND p.roles && ARRAY['engineer', 'maintenance_specialist', 'manager']::text[]));

CREATE POLICY em_faults_create ON public.faults FOR INSERT TO authenticated
WITH CHECK (created_by = (SELECT auth.uid()) AND status = 'Новая'
    AND EXISTS (SELECT 1 FROM public.profiles p WHERE p.id = (SELECT auth.uid())
        AND p.is_active AND p.roles && ARRAY['engineer', 'maintenance_specialist']::text[]));

-- USING проверяет старую строку, WITH CHECK — новую после BEFORE guard_fault.
-- Точную пару OLD/NEW, неизменяемость регистрации и started_by проверяет guard_fault.
CREATE POLICY em_faults_process ON public.faults FOR UPDATE TO authenticated
USING (status IN ('Новая', 'В работе')
    AND EXISTS (SELECT 1 FROM public.profiles p WHERE p.id = (SELECT auth.uid())
        AND p.is_active AND 'maintenance_specialist' = ANY(p.roles)))
WITH CHECK (
    ((status = 'В работе' AND started_by = (SELECT auth.uid()))
      OR (status = 'Завершена' AND completed_by = (SELECT auth.uid())))
    AND EXISTS (SELECT 1 FROM public.profiles p WHERE p.id = (SELECT auth.uid())
        AND p.is_active AND 'maintenance_specialist' = ANY(p.roles)));

CREATE POLICY em_history_read ON public.maintenance_records FOR SELECT TO authenticated
USING (EXISTS (SELECT 1 FROM public.profiles p WHERE p.id = (SELECT auth.uid())
    AND p.is_active AND p.roles && ARRAY['engineer', 'maintenance_specialist', 'manager']::text[]));

-- Вложенный INSERT выполняется внутри AFTER UPDATE заявки.
-- Глубина — дополнительное ограничение, не доказательство личности/роли.
-- Полный снимок также проверяется существующим guard_maintenance_record.
CREATE POLICY em_history_from_completion ON public.maintenance_records FOR INSERT TO authenticated
WITH CHECK (pg_catalog.pg_trigger_depth() = 1
    AND performed_by = (SELECT auth.uid())
    AND EXISTS (SELECT 1 FROM public.profiles p WHERE p.id = (SELECT auth.uid())
        AND p.is_active AND 'maintenance_specialist' = ANY(p.roles))
    AND EXISTS (
        SELECT 1 FROM public.faults f
        WHERE f.id = fault_id AND f.status = 'Завершена'
          AND f.completed_by = (SELECT auth.uid())
          AND ROW(f.equipment_id, f.equipment_label, f.completed_at,
                  f.work_description, f.result, f.completed_by)
            IS NOT DISTINCT FROM ROW(maintenance_records.equipment_id,
                  maintenance_records.equipment_label, maintenance_records.performed_at,
                  maintenance_records.work_description, maintenance_records.result,
                  maintenance_records.performed_by)
    ));

-- Удаление/редактирование оборудования, удаление заявок и изменение профилей
-- не разрешены исходной матрицей ТЗ: ни grants, ни политик для них нет.
COMMIT;
