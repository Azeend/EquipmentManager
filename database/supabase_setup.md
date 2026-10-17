# Установка EquipmentManager в Supabase PostgreSQL

> Исторический документ этапа подготовки. Описания будущих работ, числа тестов и ограничения отражают состояние того этапа. Актуальная реализация, рабочие настройки и единственный порядок установки для текущего приложения — в [backend_documentation.md](../backend_documentation.md); итог тестирования — в [backend_testing.md](../backend_testing.md). Не выполнять старые альтернативные SQL-варианты по этому документу.

## Результат проверки и ограничения

Проверены локальные schema.sql и schema_description.md, без подключения к облаку и без чтения ключей/строк подключения. SQL **не выполнялся**: локальные psql, postgres и docker в PATH не обнаружены. Ни синтаксис на сервере, ни реальное состояние проекта, ACL, event triggers и RLS исполнением не подтверждены. Далее приведены инструкции и ожидаемые результаты, а не протокол успешного запуска.

Исходные настройки сообщены пользователем: Data API включён, Automatically expose new tables выключен, automatic RLS включён. Они не проверялись через Dashboard. Отключение автоматической экспозиции не выключает Data API целиком и не заменяет аудит прав уже существующих объектов.

| Область | Вывод статического анализа / изменение |
| --- | --- |
| auth.users | profiles.id ссылается на UUID первичного ключа auth.users.id; таблица Auth не создаётся и не изменяется |
| Удаление пользователя | ON DELETE RESTRICT сознательно запрещает удаление Auth-пользователя с профилем; это не ошибка совместимости, а политика сохранения аудита |
| Порядок DDL | trim_whitespace → profiles/equipment → faults → maintenance_records → функции/триггеры → индексы → RLS/ACL → комментарии; зависимости доступны до создания использующих их объектов |
| Триггеры истории | BEFORE проверяет переход, AFTER вставляет историю в том же UPDATE; PK fault_id исключает дубль; guard истории сверяет полный снимок. Ошибка вставки истории отменяет UPDATE. Не добавлялся ON CONFLICT DO NOTHING: он мог бы скрыть нарушение целостности |
| RLS и таблицы | Четыре таблицы с ENABLE RLS, политик нет, PUBLIC/anon/authenticated лишены прав; это намеренно закрытый Data API, а не готовый клиентский CRUD |
| Функции | Подтверждён пропуск отзыва EXECUTE, выдаваемого PostgreSQL PUBLIC по умолчанию. Добавлен REVOKE для четырёх функций и PUBLIC/anon/authenticated |
| SECURITY DEFINER | В исходнике отсутствовал. Зафиксирован SECURITY INVOKER явно; повышения прав не добавлялось. search_path = pg_catalog, ссылки на таблицы квалифицированы public |
| Повторный запуск | Скрипт изначально однократный. Добавлена ранняя проверка Auth, ролей и конфликтующих таблиц/функций. Повторная установка отклоняется, не заменяет объекты и данные. Это не идемпотентная миграция |

Триггеры выполняются с правами вызывающего пользователя. Для установки и проверок предполагается доверенная роль SQL Editor — владелец объектов с доступом к Auth и обходом RLS. **Нельзя просто заменить SECURITY INVOKER на SECURITY DEFINER**, чтобы заработал будущий API: сначала нужны проверенная личность, матрица прав, безопасный search_path, квалификация имён, ограниченный EXECUTE и отдельные тесты. При текущем закрытом клиентском доступе SECURITY DEFINER не нужен.

Владелец и BYPASSRLS обходят RLS; успешный SELECT в SQL Editor не подтверждает доступ или защиту anon/authenticated. service_role не отзывается этим скриптом: его фактические grants нужно смотреть отдельно; BYPASSRLS не заменяет SQL-привилегии. Привилегированные роли могут также менять DDL/триггеры: схема не защищается от собственного администратора. Будущая авторизация по profiles.roles/is_active пока не реализована.

## 1. Предварительная проверка в SQL Editor

В Dashboard нужного проекта откройте SQL Editor → New query. Убедитесь, что выбран тестовый/целевой проект и доверенная роль postgres (или согласованная роль владельца). Не нужны API-ключи, пароль в SQL или изменение C#.

Выполните отдельно запросы чтения:

```sql
SELECT version(), current_user, current_database();
SELECT to_regclass('auth.users') AS auth_users;
SELECT a.attname, pg_catalog.format_type(a.atttypid, a.atttypmod) AS type
FROM pg_catalog.pg_attribute a
WHERE a.attrelid = to_regclass('auth.users') AND a.attname = 'id' AND NOT a.attisdropped;
SELECT rolname, rolbypassrls
FROM pg_catalog.pg_roles
WHERE rolname IN ('postgres', 'anon', 'authenticated', 'service_role');

SELECT n.nspname, c.relname, c.relkind
FROM pg_catalog.pg_class c
JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
WHERE n.nspname = 'public'
  AND c.relname IN ('profiles', 'equipment', 'faults', 'maintenance_records');

SELECT p.oid::regprocedure
FROM pg_catalog.pg_proc p
JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace
WHERE n.nspname = 'public'
  AND p.proname IN ('trim_whitespace', 'guard_fault',
                   'create_maintenance_record', 'guard_maintenance_record');
```

Ожидается auth.users с id uuid и наличие anon/authenticated. До первой установки последние два запроса должны быть пустыми. Если объекты уже существуют, **не удаляйте их и не добавляйте IF NOT EXISTS наугад**: сравните структуру, данные и права, подготовьте отдельную миграцию. Начальная проверка ловит основные конфликты; коллизии имён индексов/последовательностей могут обнаружиться позднее, но транзакция также откатит установку.

## 2. Применение схемы

1. Откройте новый SQL-запрос, вставьте **весь** database/schema.sql, включая BEGIN и COMMIT.
2. Запустите целиком кнопкой Run. Не выполняйте произвольные выделенные части: это нарушит порядок и транзакционную защиту.
3. При ошибке не продолжайте фрагментами; если соединение оставлено в ошибочной транзакции, выполните ROLLBACK. Сохраните текст ошибки и проверьте каталоги повторно.
4. При успехе выполните проверки ниже. Automatic RLS совместим с явным ENABLE RLS: повторное включение RLS допустимо. Не создавайте разрешающие политики и не выдавайте GRANT ALL клиентским ролям для устранения ожидаемого отказа доступа.

Если **предыдущая версия** schema.sql уже применена успешно, новая однократная установка намеренно остановится. Для подтверждённого ACL-пробела можно отдельно выполнить под владельцем только минимальный патч:

```sql
BEGIN;
REVOKE ALL ON FUNCTION public.trim_whitespace(text), public.guard_fault(),
  public.create_maintenance_record(), public.guard_maintenance_record()
  FROM PUBLIC, anon, authenticated;
COMMIT;
```

Это не обновляет произвольную старую схему. SECURITY INVOKER и раньше являлся значением по умолчанию; его явная запись не требует изменения уже существующих функций, если аудит подтверждает prosecdef=false. Не применяйте патч к неизвестной схеме вместо проверки её определения.

## 3. Каталоги, RLS и действующие права

```sql
-- Ожидаются 4 таблицы с relrowsecurity=true. FORCE RLS не требуется для текущего режима владельца.
SELECT c.relname, c.relrowsecurity, c.relforcerowsecurity,
       pg_catalog.pg_get_userbyid(c.relowner) AS owner
FROM pg_catalog.pg_class c
JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
WHERE n.nspname = 'public'
  AND c.relname IN ('profiles', 'equipment', 'faults', 'maintenance_records')
ORDER BY c.relname;

-- Ожидается 0 политик для этих таблиц.
SELECT * FROM pg_catalog.pg_policies
WHERE schemaname = 'public'
  AND tablename IN ('profiles', 'equipment', 'faults', 'maintenance_records');

-- Все значения any_privilege должны быть false, в том числе с учётом унаследованных прав.
SELECT r.role_name, t.table_name,
       has_table_privilege(r.role_name, 'public.' || t.table_name,
         'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER') AS any_privilege
FROM (VALUES ('anon'), ('authenticated')) r(role_name)
CROSS JOIN (VALUES ('profiles'), ('equipment'), ('faults'), ('maintenance_records')) t(table_name);

SELECT r.role_name, s.sequence_name,
       has_sequence_privilege(r.role_name, 'public.' || s.sequence_name,
         'USAGE,SELECT,UPDATE') AS any_privilege
FROM (VALUES ('anon'), ('authenticated')) r(role_name)
CROSS JOIN (VALUES ('equipment_id_seq'), ('faults_id_seq')) s(sequence_name);

SELECT r.role_name, f.signature,
       has_function_privilege(r.role_name, f.signature, 'EXECUTE') AS can_execute
FROM (VALUES ('anon'), ('authenticated')) r(role_name)
CROSS JOIN (VALUES ('public.trim_whitespace(text)'), ('public.guard_fault()'),
  ('public.create_maintenance_record()'), ('public.guard_maintenance_record()')) f(signature);

-- Ожидается false для security_definer и search_path=pg_catalog у всех четырёх функций.
SELECT p.oid::regprocedure AS function_name, p.prosecdef AS security_definer,
       p.proconfig, p.proacl
FROM pg_catalog.pg_proc p
JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace
WHERE n.nspname = 'public'
  AND p.proname IN ('trim_whitespace', 'guard_fault',
                   'create_maintenance_record', 'guard_maintenance_record');

-- Ожидаются три пользовательских триггера в сумме.
SELECT t.tgname, c.relname, t.tgenabled, pg_get_triggerdef(t.oid)
FROM pg_catalog.pg_trigger t
JOIN pg_catalog.pg_class c ON c.oid = t.tgrelid
JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
WHERE n.nspname = 'public' AND c.relname IN ('faults', 'maintenance_records')
  AND NOT t.tgisinternal;
```

Если действующие права неожиданно true, проверьте наследование ролей, ACL и локальные event triggers. Скрипт не меняет глобальные DEFAULT PRIVILEGES и не отнимает права на посторонние объекты. На уровне проекта могут существовать дополнительные функции/таблицы: этот аудит не распространяется на них.

Для проверки отказа таблицы можно запустить отдельным запросом (ожидается permission denied):

```sql
BEGIN;
SET LOCAL ROLE anon;
SELECT * FROM public.equipment LIMIT 1;
ROLLBACK;
```

Повторите с authenticated. После ожидаемой ошибки выполните отдельный ROLLBACK, если редактор не завершил транзакцию сам. Отказ обусловлен ACL; он не доказывает отдельно работу RLS. Режим без политик означает запрет строк при появлении grants, но выдача временных grants для такого теста здесь намеренно не предлагается. Не отключайте RLS.

## 4. Проверка переходов и единственной истории с откатом

Потребуется заранее подготовленный **отдельный тестовый Auth-пользователь** и профиль с display_name='SQL smoke test', созданный доверенным администратором. Не создавайте фиктивные записи напрямую в auth.users. Для профиля используется реальный UUID этого тестового пользователя; роли назначаются только доверенным администратором, а не пользователем себе. Приведённый тест работает под владельцем и проверяет целостность, **не авторизацию бизнес-ролей**.

Скрипт требует ровно один такой профиль; если его нет, завершится понятной ошибкой без записей. Все DML-изменения теста откатываются. Identity-последовательности могут оставить пропуски даже после ROLLBACK — это нормально.

```sql
BEGIN;
DO $$
DECLARE
    test_user uuid;
    equipment_id_value integer;
    fault_id_value integer;
    version_value integer;
    rejected boolean;
BEGIN
    IF (SELECT count(*) FROM public.profiles WHERE display_name = 'SQL smoke test') <> 1 THEN
        RAISE EXCEPTION 'Требуется ровно один отдельный тестовый профиль SQL smoke test';
    END IF;
    SELECT id INTO STRICT test_user FROM public.profiles WHERE display_name = 'SQL smoke test';
    INSERT INTO public.equipment(name, inventory_number, location, status)
      VALUES ('SQL smoke test', 'SMOKE-' || pg_catalog.txid_current()::text, 'Тест', 'Исправно')
      RETURNING id INTO equipment_id_value;
    INSERT INTO public.faults(equipment_id, description, created_by)
      VALUES (equipment_id_value, 'Проверка переходов', test_user)
      RETURNING id INTO fault_id_value;

    rejected := false;
    BEGIN
        UPDATE public.faults SET status = 'Завершена', completed_by = test_user,
          work_description = 'Работы', result = 'Результат' WHERE id = fault_id_value;
    EXCEPTION WHEN raise_exception THEN
        rejected := true;
    END;
    IF NOT rejected THEN RAISE EXCEPTION 'Не отклонён пропуск этапа'; END IF;

    UPDATE public.faults SET status = 'В работе', started_by = test_user
      WHERE id = fault_id_value AND version = 1;
    IF NOT FOUND THEN RAISE EXCEPTION 'Не выполнено начало работ'; END IF;

    rejected := false;
    BEGIN
        UPDATE public.faults SET status = 'Завершена', completed_by = test_user,
          work_description = ' ', result = 'Результат' WHERE id = fault_id_value;
    EXCEPTION WHEN check_violation THEN
        rejected := true;
    END;
    IF NOT rejected THEN RAISE EXCEPTION 'Не отклонены пустые работы'; END IF;
    IF EXISTS (SELECT 1 FROM public.maintenance_records WHERE fault_id = fault_id_value) THEN
        RAISE EXCEPTION 'При отказе появилась история';
    END IF;

    UPDATE public.faults SET status = 'Завершена', completed_by = test_user,
      work_description = 'Диагностика', result = 'Исправлено'
      WHERE id = fault_id_value AND version = 2;
    IF NOT FOUND THEN RAISE EXCEPTION 'Не выполнено завершение'; END IF;
    SELECT version INTO version_value FROM public.faults WHERE id = fault_id_value;
    IF version_value <> 3 THEN RAISE EXCEPTION 'Неверная версия'; END IF;
    IF (SELECT count(*) FROM public.maintenance_records WHERE fault_id = fault_id_value) <> 1 THEN
        RAISE EXCEPTION 'История не единственная';
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM public.maintenance_records m JOIN public.faults f ON f.id = m.fault_id
        WHERE f.id = fault_id_value AND m.equipment_id = f.equipment_id
          AND m.equipment_label = f.equipment_label AND m.performed_at = f.completed_at
          AND m.work_description = f.work_description AND m.result = f.result
          AND m.performed_by = f.completed_by
    ) THEN RAISE EXCEPTION 'История не совпадает с заявкой'; END IF;

    rejected := false;
    BEGIN
        UPDATE public.faults SET status = 'Завершена' WHERE id = fault_id_value;
    EXCEPTION WHEN raise_exception THEN
        rejected := true;
    END;
    IF NOT rejected THEN RAISE EXCEPTION 'Не отклонено повторное завершение'; END IF;
    RAISE NOTICE 'Переходы, обязательные работы, версии и единственная история проверены; далее ROLLBACK';
END;
$$;
ROLLBACK;
```

Ожидаемые исключения перехватываются только внутри отрицательных сценариев и проверяются флагом. Неожиданные исключения не подавляются. При любой ошибке выполните ROLLBACK отдельно. Успешный NOTICE означает только перечисленные проверки, не весь перечень ниже.

## 5. Остальные обязательные проверки перед подключением приложения

| Проверка | Ожидаемый результат |
| --- | --- |
| Пустые имя/номер/место и неизвестное состояние | NOT NULL/CHECK, данные не записаны |
| INV-1001 и пробелы/другой регистр | UNIQUE; проверить также Unicode-пробелы, кириллицу и collation |
| Пустой результат завершения | CHECK, заявка остаётся в работе, истории нет |
| Ошибка именно INSERT истории | В отдельной тестовой транзакции временно добавить блокирующий BEFORE INSERT-триггер, завершить заявку и убедиться в полном откате UPDATE и отсутствии истории; затем откатить весь тест. Не отключать рабочие guards |
| Две параллельные команды завершения одной версии | С WHERE version/status только одна изменяет строку, история одна; вторая меняет 0 строк |
| DELETE/UPDATE истории | Отказ guard_maintenance_record; прямой второй INSERT той же истории — отказ PK |
| Удаление оборудования/профиля со связями | FK RESTRICT; никаких каскадных потерь |
| Повтор schema.sql | Понятный отказ начального блока; существующие строки и определения не заменены |
| Права API | anon/authenticated не имеют прав таблиц, последовательностей и EXECUTE функций; RLS включён, политик нет |

**Эти проверки пока не выполнены.** C#-тесты и dotnet build не проверяют PostgreSQL-триггеры или Supabase RLS, поэтому в этом SQL-аудите они не запускались и не выдаются за проверку схемы. Будущий сервис обязан проверять личность, активность, роль и ожидаемую версию; отдельно вставлять историю ему не нужно. При выдаче непривилегированной серверной роли UPDATE необходимо отдельно спроектировать доступ invoker-триггера к истории и вспомогательным функциям.

## Официальные источники

- [Supabase: пользовательские данные и auth.users](https://supabase.com/docs/guides/auth/managing-user-data).
- [Supabase: RLS, роли и политики](https://supabase.com/docs/guides/database/postgres/row-level-security).
- [Supabase: защита Data API и grants](https://supabase.com/docs/guides/api/securing-your-api).
- [Supabase: права функций и SECURITY INVOKER/DEFINER](https://supabase.com/docs/guides/database/functions).
- [PostgreSQL: CREATE FUNCTION, EXECUTE по умолчанию и безопасность DEFINER](https://www.postgresql.org/docs/current/sql-createfunction.html).

Изменены только database/schema.sql, database/schema_description.md и этот документ. Подключений к облаку, использования секретов, изменений C#, commit и push не выполнялось.
