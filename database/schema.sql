-- Лабораторная №5. Проект для PostgreSQL в Supabase; выполнять один раз.
-- Требуется существующая auth.users(id uuid). Расширения не устанавливаются.
BEGIN;

-- Однократная установка: не маскируем отличия существующей схемы через IF NOT EXISTS.
DO $$
BEGIN
    IF to_regclass('auth.users') IS NULL THEN
        RAISE EXCEPTION 'Требуется Supabase Auth с существующей таблицей auth.users';
    END IF;
    IF (SELECT count(*) FROM pg_catalog.pg_roles WHERE rolname IN ('anon', 'authenticated')) <> 2 THEN
        RAISE EXCEPTION 'Требуются роли Supabase anon и authenticated';
    END IF;
    IF to_regclass('public.profiles') IS NOT NULL
        OR to_regclass('public.equipment') IS NOT NULL
        OR to_regclass('public.faults') IS NOT NULL
        OR to_regclass('public.maintenance_records') IS NOT NULL
        OR to_regprocedure('public.trim_whitespace(text)') IS NOT NULL
        OR to_regprocedure('public.guard_fault()') IS NOT NULL
        OR to_regprocedure('public.create_maintenance_record()') IS NOT NULL
        OR to_regprocedure('public.guard_maintenance_record()') IS NOT NULL THEN
        RAISE EXCEPTION 'Объекты EquipmentManager уже существуют. Повторная установка запрещена; требуется отдельная миграция';
    END IF;
END;
$$;

-- Пробельные символы .NET Char.IsWhiteSpace / String.Trim (Unicode).
CREATE FUNCTION public.trim_whitespace(value text) RETURNS text
LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE SECURITY INVOKER
SET search_path = pg_catalog
AS $$ SELECT btrim(value, U&'\0009\000A\000B\000C\000D\0020\0085\00A0\1680\2000\2001\2002\2003\2004\2005\2006\2007\2008\2009\200A\2028\2029\202F\205F\3000') $$;

CREATE TABLE public.profiles (
    id uuid PRIMARY KEY REFERENCES auth.users(id) ON DELETE RESTRICT,
    display_name text NOT NULL CHECK (public.trim_whitespace(display_name) <> ''),
    roles text[] NOT NULL DEFAULT ARRAY[]::text[],
    is_active boolean NOT NULL DEFAULT true,
    CONSTRAINT profiles_roles_check CHECK (
        roles <@ ARRAY['engineer', 'maintenance_specialist', 'manager']::text[]
        AND array_position(roles, NULL) IS NULL
        AND array_ndims(roles) IS NOT DISTINCT FROM CASE WHEN cardinality(roles) = 0 THEN NULL::integer ELSE 1 END
    )
);

CREATE TABLE public.equipment (
    id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY CHECK (id > 0),
    name text NOT NULL CHECK (public.trim_whitespace(name) <> ''),
    inventory_number text NOT NULL CHECK (public.trim_whitespace(inventory_number) <> ''),
    normalized_inventory_number text GENERATED ALWAYS AS
        (lower(public.trim_whitespace(inventory_number))) STORED,
    location text NOT NULL CHECK (public.trim_whitespace(location) <> ''),
    status text NOT NULL CHECK (status IN ('Исправно', 'Требует обслуживания', 'Неисправно')),
    CONSTRAINT equipment_inventory_number_unique UNIQUE (normalized_inventory_number)
);

CREATE TABLE public.faults (
    id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY CHECK (id > 0),
    equipment_id integer NOT NULL REFERENCES public.equipment(id) ON DELETE RESTRICT,
    equipment_label text NOT NULL CHECK (public.trim_whitespace(equipment_label) <> ''),
    description text NOT NULL CHECK (public.trim_whitespace(description) <> ''),
    status text NOT NULL DEFAULT 'Новая' CHECK (status IN ('Новая', 'В работе', 'Завершена')),
    created_by uuid NOT NULL REFERENCES public.profiles(id) ON DELETE RESTRICT,
    started_by uuid REFERENCES public.profiles(id) ON DELETE RESTRICT,
    completed_by uuid REFERENCES public.profiles(id) ON DELETE RESTRICT,
    created_at timestamptz NOT NULL DEFAULT statement_timestamp(),
    started_at timestamptz,
    completed_at timestamptz,
    work_description text NOT NULL DEFAULT '',
    result text NOT NULL DEFAULT '',
    version integer NOT NULL DEFAULT 1 CHECK (version > 0),
    CONSTRAINT faults_id_equipment_unique UNIQUE (id, equipment_id),
    CONSTRAINT faults_timeline_check CHECK (
        (started_at IS NULL OR started_at >= created_at)
        AND (completed_at IS NULL OR completed_at >= started_at)
    ),
    CONSTRAINT faults_state_check CHECK (
        (status = 'Новая' AND started_at IS NULL AND started_by IS NULL
            AND completed_at IS NULL AND completed_by IS NULL AND work_description = '' AND result = '')
        OR (status = 'В работе' AND started_at IS NOT NULL AND started_by IS NOT NULL
            AND completed_at IS NULL AND completed_by IS NULL AND work_description = '' AND result = '')
        OR (status = 'Завершена' AND started_at IS NOT NULL AND started_by IS NOT NULL
            AND completed_at IS NOT NULL AND completed_by IS NOT NULL
            AND public.trim_whitespace(work_description) <> '' AND public.trim_whitespace(result) <> '')
    )
);

CREATE TABLE public.maintenance_records (
    fault_id integer PRIMARY KEY,
    equipment_id integer NOT NULL REFERENCES public.equipment(id) ON DELETE RESTRICT,
    equipment_label text NOT NULL CHECK (public.trim_whitespace(equipment_label) <> ''),
    performed_at timestamptz NOT NULL,
    work_description text NOT NULL CHECK (public.trim_whitespace(work_description) <> ''),
    result text NOT NULL CHECK (public.trim_whitespace(result) <> ''),
    performed_by uuid NOT NULL REFERENCES public.profiles(id) ON DELETE RESTRICT,
    CONSTRAINT maintenance_fault_equipment_fk FOREIGN KEY (fault_id, equipment_id)
        REFERENCES public.faults(id, equipment_id) ON DELETE RESTRICT
);

-- Версия увеличивается БД; ожидаемую версию приложение передаёт в WHERE UPDATE.
CREATE FUNCTION public.guard_fault() RETURNS trigger
LANGUAGE plpgsql SECURITY INVOKER SET search_path = pg_catalog AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.status <> 'Новая' OR NEW.version <> 1 THEN
            RAISE EXCEPTION 'Заявка должна создаваться новой с версией 1';
        END IF;
        NEW.created_at := statement_timestamp();
        SELECT e.name || ' (' || e.inventory_number || ')' INTO NEW.equipment_label
        FROM public.equipment e WHERE e.id = NEW.equipment_id;
    ELSE
        IF OLD.status = 'Завершена'
            OR NOT ((OLD.status = 'Новая' AND NEW.status = 'В работе')
                OR (OLD.status = 'В работе' AND NEW.status = 'Завершена')) THEN
            RAISE EXCEPTION 'Разрешены только переходы Новая → В работе → Завершена';
        END IF;
        IF ROW(NEW.equipment_id, NEW.equipment_label, NEW.description, NEW.created_by, NEW.created_at)
            IS DISTINCT FROM ROW(OLD.equipment_id, OLD.equipment_label, OLD.description, OLD.created_by, OLD.created_at) THEN
            RAISE EXCEPTION 'Исходные сведения заявки неизменяемы';
        END IF;
        NEW.version := OLD.version + 1;
        IF NEW.status = 'В работе' THEN
            NEW.started_at := statement_timestamp();
        ELSE
            IF ROW(NEW.started_at, NEW.started_by) IS DISTINCT FROM ROW(OLD.started_at, OLD.started_by) THEN
                RAISE EXCEPTION 'Сведения о начале работ неизменяемы';
            END IF;
            NEW.completed_at := statement_timestamp();
        END IF;
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER faults_guard BEFORE INSERT OR UPDATE ON public.faults
FOR EACH ROW EXECUTE FUNCTION public.guard_fault();

CREATE FUNCTION public.create_maintenance_record() RETURNS trigger
LANGUAGE plpgsql SECURITY INVOKER SET search_path = pg_catalog AS $$
BEGIN
    IF NEW.status = 'Завершена' THEN
        INSERT INTO public.maintenance_records
            (fault_id, equipment_id, equipment_label, performed_at, work_description, result, performed_by)
        VALUES (NEW.id, NEW.equipment_id, NEW.equipment_label, NEW.completed_at,
            NEW.work_description, NEW.result, NEW.completed_by);
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER faults_create_history AFTER UPDATE ON public.faults
FOR EACH ROW EXECUTE FUNCTION public.create_maintenance_record();

CREATE FUNCTION public.guard_maintenance_record() RETURNS trigger
LANGUAGE plpgsql SECURITY INVOKER SET search_path = pg_catalog AS $$
BEGIN
    IF TG_OP <> 'INSERT' THEN
        RAISE EXCEPTION 'Изменение и удаление истории обслуживания запрещены';
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM public.faults f WHERE f.id = NEW.fault_id AND f.status = 'Завершена'
        AND ROW(f.equipment_id, f.equipment_label, f.completed_at, f.work_description, f.result, f.completed_by)
            IS NOT DISTINCT FROM ROW(NEW.equipment_id, NEW.equipment_label, NEW.performed_at,
                NEW.work_description, NEW.result, NEW.performed_by)
    ) THEN
        RAISE EXCEPTION 'История должна совпадать с завершённой заявкой';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER maintenance_records_guard BEFORE INSERT OR UPDATE OR DELETE ON public.maintenance_records
FOR EACH ROW EXECUTE FUNCTION public.guard_maintenance_record();

-- B-tree: равенство, фильтры, очередь и история. Подстроку %текст% не ускоряет.
CREATE INDEX equipment_name_search_idx ON public.equipment (lower(name) text_pattern_ops);
CREATE INDEX equipment_inventory_search_idx ON public.equipment (normalized_inventory_number text_pattern_ops);
CREATE INDEX equipment_status_idx ON public.equipment (status);
CREATE INDEX faults_equipment_idx ON public.faults (equipment_id, id DESC);
CREATE INDEX faults_status_idx ON public.faults (status, id DESC);
CREATE INDEX faults_created_by_idx ON public.faults (created_by);
CREATE INDEX faults_started_by_idx ON public.faults (started_by);
CREATE INDEX faults_completed_by_idx ON public.faults (completed_by);
CREATE INDEX maintenance_equipment_date_idx ON public.maintenance_records (equipment_id, performed_at DESC, fault_id DESC);
CREATE INDEX maintenance_performed_by_idx ON public.maintenance_records (performed_by);

-- Прямой Data API закрыт: политик нет, выдача ролей клиенту не разрешена.
-- Будущий серверный доступ и матрица прав требуют отдельной настройки.
ALTER TABLE public.profiles ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.equipment ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.faults ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.maintenance_records ENABLE ROW LEVEL SECURITY;
REVOKE ALL ON TABLE public.profiles, public.equipment, public.faults, public.maintenance_records FROM PUBLIC, anon, authenticated;
REVOKE ALL ON SEQUENCE public.equipment_id_seq, public.faults_id_seq FROM PUBLIC, anon, authenticated;
-- Функции также могут быть доступны через Data API; RLS не заменяет EXECUTE ACL.
REVOKE ALL ON FUNCTION public.trim_whitespace(text), public.guard_fault(),
    public.create_maintenance_record(), public.guard_maintenance_record()
    FROM PUBLIC, anon, authenticated;

COMMENT ON TABLE public.profiles IS 'Профили Supabase Auth: отображаемое имя и назначенные сервером роли, без паролей';
COMMENT ON TABLE public.equipment IS 'Реестр оборудования; удаление связанных объектов запрещено';
COMMENT ON TABLE public.faults IS 'Заявки с последовательными переходами и оптимистической версией';
COMMENT ON TABLE public.maintenance_records IS 'Неизменяемый снимок завершения; ключ равен FaultId модели C#';
COMMENT ON COLUMN public.equipment.normalized_inventory_number IS 'lower после удаления крайних Unicode-пробелов; зависит от collation базы';
COMMENT ON COLUMN public.faults.equipment_label IS 'Снимок имени и номера на момент регистрации';
COMMENT ON COLUMN public.profiles.roles IS 'engineer — инженер; maintenance_specialist — специалист; manager — руководитель; пусто — нет прав';
COMMIT;
