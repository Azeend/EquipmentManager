# Этап 6.1: подготовка equipment_api и серверного RLS

> Исторический документ этапа подготовки. Описания будущих работ, числа тестов и ограничения отражают состояние того этапа. Актуальная реализация, рабочие настройки и единственный порядок установки для текущего приложения — в [backend_documentation.md](../backend_documentation.md); итог тестирования — в [backend_testing.md](../backend_testing.md). Не выполнять старые альтернативные SQL-варианты по этому документу.

Дополнительный аудит перед первым запуском: [006_1_security_audit.md](006_1_security_audit.md). Предварительная диагностика без создания роли: [006_1_equipment_api_diagnostics.sql](006_1_equipment_api_diagnostics.sql). Read-only проверки после миграции на тестовом стенде: [006_1_equipment_api_rls_readonly.psql](006_1_equipment_api_rls_readonly.psql). Никакие из этих SQL-сценариев автоматически не выполнялись.

## Статус и объём

Миграция [006_1_equipment_api_security.sql](006_1_equipment_api_security.sql) подготовлена для ручного развёртывания поверх существующей schema.sql. **Агент SQL не выполнял; серверный разбор и выполнение миграции не проверены.** Пользователь передал результаты реальной диагностики rls_auto_enable; они учтены в исключении ниже. Приложение, модели, компоненты, JWT-настройки, оборудование, заявки и история этой правкой не изменены. equipment_app и её политика чтения профилей не изменяются.

Решение от 17.10.2026: инфраструктурные rls_auto_enable/ensure_rls и PUBLIC EXECUTE сохраняются, REVOKE не выполняется. В проверке доступных SECURITY DEFINER разрешён только объект `public.rls_auto_enable()` с MD5 `846ec2fbff751d24b890656d63c3a9bf`, владельцем postgres, точным search_path=pg_catalog и проверенной событийной привязкой. Полные условия, риски и порядок проверки: [актуальный протокол](006_1_rls_auto_enable_audit.md). Файл с суффиксом `_revoke.sql` — отклонённый исторический вариант, запускать его перед миграцией нельзя. Остальные DEFINER-функции исключением не покрываются.

Пользователь сообщил об успешном завершении шага 5: реальные HTTP-коды 401, 401, 200, 200. Это исходное условие, а не новый вход в рамках этапа 6.1.

Сейчас выдаются только права для оборудования и чтения собственного профиля внутри RLS. Перевод всего рабочего режима на PostgreSQL остаётся следующим этапом. Не переключать только оборудование, пока заявки и история работают в памяти: FK PostgreSQL не видят связи из памяти. Тестовые данные не переносятся автоматически.

## Доверенная граница

equipment_api — отдельный серверный PostgreSQL login без SUPERUSER, BYPASSRLS, CREATEDB, CREATEROLE, REPLICATION, членства в других ролях и владения прикладными таблицами. NOINHERIT не заменяет аудит членства и PUBLIC. Существующая equipment_app остаётся отдельным читателем профиля для текущего JWT-обработчика.

Учтено поведение PostgreSQL 17: при создании роли не-суперпользователем CREATEROLE сервер автоматически даёт создателю членство в новой роли с ADMIN TRUE, SET FALSE, INHERIT FALSE. Миграция допускает только такую административную связь с текущим доверенным создателем, без наследования и SET ROLE; любые другие члены или членство equipment_api в других ролях вызывают откат. Это не расширяет полномочия equipment_api. Если createrole_self_grant администратора включает SET/INHERIT, требуется отдельно разобрать его настройки; миграция не ослабляет проверку автоматически.

Прикладной API проверяет подпись ES256, issuer, audience, срок JWT и sub, затем права через public.profiles. Будущий адаптер БД передаёт **только sub уже проверенного ClaimsPrincipal**, разобранный как Guid, в транзакционный контекст `equipment.user_id`. Поля DTO, URL, query string, заголовок с произвольным UUID, user_metadata и app_metadata для этого не используются. Бизнес-роли в контекст не копируются: RLS читает актуальные roles/is_active из БД.

Custom GUC **не проверяет JWT** и технически может быть установлен любым владельцем credentials equipment_api. RLS защищает от пропущенной проверки в обычном SQL приложения, но не от компрометации доверенного backend, его login или SQL injection, позволяющей выполнять произвольные команды. Именно поэтому login доступен только серверу, все запросы параметризованы, произвольный SQL/RPC не публикуется. Изменять profiles.roles/is_active роль не может даже при подстановке другого UUID.

В схеме equipment_api_security две STABLE SECURITY INVOKER-функции с фиксированным search_path=pg_catalog и квалифицированными ссылками. current_profile_id возвращает NULL без контекста/при пустом значении; неверный формат UUID приводит к ошибке запроса. has_any_role читает защищённый профиль с правами caller. Политика profiles её не вызывает, поэтому цикла RLS нет. Новые SECURITY DEFINER-функции не создаются.

## Матрица прав

| Объект | ACL equipment_api | Дополнительное ограничение RLS |
| --- | --- | --- |
| profiles | SELECT только id, roles, is_active | Только собственный активный профиль с известной ролью |
| equipment | SELECT | engineer, maintenance_specialist или manager активного профиля |
| equipment | INSERT/UPDATE только name, inventory_number, location, status | Только engineer; UPDATE проверяет USING и WITH CHECK |
| equipment | DELETE | Только engineer; FK запрещают удаление связанных объектов |
| equipment_id_seq | USAGE, без SELECT/UPDATE | RLS на последовательности не распространяется; login доверенный серверный |
| trim_whitespace | EXECUTE | Чистая функция для CHECK/generated-выражения |
| public.rls_auto_enable() | EXECUTE через существующий PUBLIC; новых GRANT нет | Только точное аудированное определение и привязка event trigger; это принятый ACL-доступ, не право обычного RPC |
| faults / maintenance_records / faults_id_seq | Прав нет | Команды заявок и истории этим этапом не включаются |
| profiles: любые изменения | Прав нет, политик записи нет | Самостоятельное назначение ролей невозможно через обычный DML |

INSERT не принимает id и normalized_inventory_number; ID назначается identity, нормализация вычисляется БД. USAGE последовательности позволяет nextval, поэтому пропуски ID допустимы, включая ROLLBACK и неуспешные INSERT. Непрерывность номеров не обещается.

## Совместимость политик и ограничений

- Новые пять политик адресованы только equipment_api. Создание роли не включает её в authenticated, equipment_app или другие роли. Политики этих именованных ролей к ней не применяются. Существующие политики и grants не заменяются и не отзываются.
- Любая PUBLIC-политика на четырёх таблицах приводит к отказу миграции: permissive-политики объединяются через OR и могут ослабить ограничения. Дополнительные restrictive-политики способны сузить доступ. После любых изменений политики, членство и PUBLIC нужно проверять заново.
- equipment_app продолжает читать три столбца профилей по своей прежней политике. Для equipment_api собственная политика profiles нужна, чтобы SECURITY INVOKER helper мог проверить роль без открытия всех профилей.
- Старый security_policies.sql предназначен для прямого Data API. Не запускать его ни перед этой миграцией ради Npgsql, ни после: его preflight уже отклоняет существующие политики. Если его именованные authenticated-политики были применены раньше отдельно, новая роль их не наследует; их доступ к Data API сохраняется, а не закрывается этой миграцией. Это отдельная граница безопасности.
- Новая схема не должна входить в exposed schemas Supabase Data API. В ней нет публичных EXECUTE/USAGE для PUBLIC, anon, authenticated. Миграция не меняет настройки Dashboard и не выдаёт клиентским ролям доступ.
- UNIQUE equipment_inventory_number_unique и FK ON DELETE RESTRICT не меняются. Дубликат окончательно отклоняет БД, включая конкурентные операции. Ожидаемый SQLSTATE уникальности — 23505; связанного удаления — 23503. RLS не отменяет проверку FK, даже когда equipment_api не может читать таблицу заявок. Детали SQL-ошибок нельзя выдавать клиенту.
- guard_fault, create_maintenance_record и guard_maintenance_record остаются SECURITY INVOKER. Миграция проверяет их наличие/включённость и отсутствие SECURITY DEFINER; определения не переписывает. Новая роль не получает EXECUTE этих trigger-функций и права записи заявок/истории.
- Поэтому завершение заявки под equipment_api сейчас **не должно работать**. Будущая отдельная миграция должна предоставить узкие права записи заявки и вложенного INSERT истории, SELECT для guard и соответствующий RLS. Иначе AFTER-триггер откатит завершение. Историю создаёт только триггер; не добавлять второй INSERT из C#. Сохраняются PK fault_id, составной FK и проверка точного снимка. Runtime-проверка атомарности относится к следующему этапу.

## Развёртывание вручную

1. Сначала проверить миграцию в отдельном тестовом проекте с той же схемой, владельцем и настройками PostgreSQL 17. Не использовать рабочие данные для пробных CRUD.
2. Сохранить снимок схемы, политик и ACL до установки: pg_policies, pg_get_triggerdef, определения четырёх функций, pg_roles, pg_auth_members, pg_class.relacl, pg_attribute.attacl, pg_proc.proacl и default privileges. Не экспортировать пароли/хэши из pg_authid.
3. Сверить schema.sql с фактической схемой: CHECK состояний/обязательных полей, generated-нормализацию, identity, UNIQUE и все FK. Preflight проверяет часть инвариантов, но не доказывает полную идентичность определений.
4. Убедиться, что роли equipment_api и схемы equipment_api_security ещё нет. Политики PUBLIC недопустимы. Проверить владельца объектов и все доступные SECURITY DEFINER/RPC; миграция отклоняет доступные новой роли пользовательские DEFINER-функции, кроме одного точного совпадения rls_auto_enable по актуальному протоколу. Общая диагностика продолжит показывать эту функцию: её would_block_migration — сырой ACL-сигнал, не учитывающий адресное исключение. Любое несовпадение условий требует остановки; хеш автоматически не обновлять, PUBLIC EXECUTE не отзывать.
5. В Supabase SQL Editor выбрать нужный проект/базу postgres и выполнить **весь** файл 006_1_equipment_api_security.sql доверенным администратором. Файл содержит BEGIN/COMMIT: ошибка откатывает создание роли, схемы, grants и политик. В psql использовать ON_ERROR_STOP. При ошибке в интерактивной сессии выполнить ROLLBACK перед дальнейшими действиями.
6. Повторный запуск намеренно запрещён. Существующую роль не пересоздавать через DROP и не подменять миграцию набором IF NOT EXISTS.
7. Только после успешной проверки прав отдельно назначить пароль через интерактивный psql:

```psql
\password equipment_api
```

Ввести новый уникальный пароль в скрытые приглашения. Не писать ALTER ROLE ... PASSWORD с настоящим значением в SQL Editor, скрипте, чате или истории команд. Для psql использовать сеанс без небезопасного журналирования клиентской стороны; администратор отдельно проверяет серверную политику аудита/маскирования чувствительных команд.

8. Для Session pooler согласованного проекта login будет `equipment_api.cyzanurmyswswbicjiaf`, host aws-1-eu-central-1.pooler.supabase.com, порт 5432, база postgres. Подключение — VerifyFull и официальный Root Certificate. Не заменять hostname IP-адресом и не включать Trust Server Certificate.
9. В завершённой реализации используется **ConnectionStrings:EquipmentCrud** в User Secrets (Development) либо **ConnectionStrings__EquipmentCrud** на сервере. Старое проектное имя ConnectionStrings:Equipment не используется. ConnectionStrings:Profiles остаётся отдельным подключением читателя профилей. Текущий порядок включения приведён в актуальной Backend-документации.

PUBLIC-права не отзываются автоматически. Проверка учитывает и USAGE схемы, и права объекта. Узкое исключение оставлено только для двух read-only views extensions.pg_stat_statements и extensions.pg_stat_statements_info при подтверждённой принадлежности одноимённому расширению. Любой иной неожиданный доступ останавливает миграцию. Системные каталоги и стандартные общедоступные функции PostgreSQL не являются полностью закрытым sandbox; административные изменения, extension/default grants и новые RPC требуют повторного аудита.

## Обязательный контракт будущего Npgsql-адаптера

Каждый запрос, включая SELECT, использует выделенное открытое соединение и явную транзакцию. На том же соединении/транзакции, **до любых запросов прикладных данных**, сервер выполняет параметризованную команду:

```sql
SELECT set_config('equipment.user_id', $1, true);
```

`$1` — строковое представление Guid из проверенного sub. is_local=true ограничивает значение текущей транзакцией. Результат команды и параметры не журналировать. Команда вне явной транзакции бессмысленна для следующего запроса: её локальный контекст уже закончится.

Далее SELECT/CRUD и COMMIT; при исключении, отмене или разрыве — ROLLBACK/Dispose транзакции и соединения. Не использовать session-level SET, set_config(..., false), контекст в connection Options, SET ROLE authenticated или request.jwt.claims. auth.uid() этой миграцией не используется. Контекст задаётся заново в каждой транзакции, а не один раз на circuit/пользователя/соединение. Нельзя параллельно обслуживать пользователей на одном соединении или возвращать его в пул с открытой транзакцией.

No Reset On Close должен оставаться false; не отключать стандартный сброс состояния Npgsql. Не полагаться только на reset: SET LOCAL может восстановить ранее установленное session-level значение, поэтому session-level контекст запрещён, а обязательная установка проверенного sub перед каждым доступом должна обеспечиваться одним общим адаптером. После savepoint rollback, откатывающего установку контекста, повторить установку либо завершить транзакцию. Не оборачивать её в savepoint с последующим продолжением без контекста.

SQL-миграция не может доказать, что значение пришло из настоящего JWT, или запретить доверенному login устанавливать GUC на уровне сессии. До реализации и тестов указанного контракта приложение на эту роль не переключать.

## Проверка после установки — подготовлена, не выполнена

Проверять под настоящим login equipment_api с VerifyFull. SQL Editor обычно работает с повышенными правами и не доказывает корректность RLS. Ручная установка тестового UUID допустима только администратором на тестовом стенде; в приложении UUID всегда берётся из проверенного JWT.

Безопасные запросы метаданных (не содержат пароля/JWT):

```sql
SELECT current_user, rolsuper, rolbypassrls, rolcreatedb, rolcreaterole,
       rolreplication, rolinherit
FROM pg_catalog.pg_roles WHERE rolname = current_user;

SELECT schemaname, tablename, policyname, roles, cmd, permissive, qual, with_check
FROM pg_catalog.pg_policies
WHERE schemaname = 'public'
  AND tablename IN ('profiles', 'equipment', 'faults', 'maintenance_records');

SELECT attname,
       has_column_privilege(current_user, attrelid, attnum, 'SELECT') AS can_select,
       has_column_privilege(current_user, attrelid, attnum, 'INSERT') AS can_insert,
       has_column_privilege(current_user, attrelid, attnum, 'UPDATE') AS can_update
FROM pg_catalog.pg_attribute
WHERE attrelid = 'public.profiles'::regclass AND attnum > 0 AND NOT attisdropped;
```

Повторить column-проверку для equipment; отдельно проверить has_table_privilege DELETE/TRUNCATE/TRIGGER/REFERENCES и has_sequence_privilege. Не считать table-level INSERT=false доказательством отсутствия column-level INSERT.

| Проверка на тестовом стенде | Ожидаемый результат |
| --- | --- |
| current_user и атрибуты | equipment_api; все перечисленные административные флаги и rolinherit — false |
| Членство и владение | Нет членства в других ролях/прикладных членов; допустима только описанная ADMIN-связь создателя, нет владения таблицами и CREATE в пользовательских схемах |
| equipment_app до/после | ACL, членство и em_profiles_equipment_app_read не изменены; прежнее чтение работает |
| SELECT без контекста или с пустым контекстом | 0 строк equipment/profiles |
| INSERT без контекста | Отказ RLS, ни одной созданной строки |
| UPDATE/DELETE без контекста | 0 изменённых строк; не считать это успехом API |
| Некорректный UUID | Отказ запроса, без перехода к разрешающему режиму |
| Несуществующий/inactive/roles=[] профиль | Чтение пустое, изменения не разрешены |
| Активный engineer | Чтение и CRUD несвязанного оборудования разрешены |
| Активный manager/maintenance_specialist без engineer | SELECT разрешён, INSERT отклонён, UPDATE/DELETE не меняют строки |
| Совмещённые роли с engineer | CRUD оборудования разрешён |
| SELECT другого профиля / display_name | Чужой профиль не виден; display_name запрещён по ACL |
| INSERT/UPDATE/DELETE profiles, в том числе roles/is_active | Отказ ACL, независимо от контекста |
| Запись id / normalized_inventory_number | Отказ; INSERT с обычными полями получает ID от БД |
| Пустые поля, неверное состояние, дубликат | CHECK/UNIQUE отклоняют; для дубликата ожидается 23505 |
| Два конкурентных одинаковых номера | Только один INSERT успешен, второй — 23505 |
| DELETE оборудования с существующей заявкой/историей | 23503; оборудование и связанные записи сохранены |
| Любая операция faults/maintenance_records под equipment_api | Отказ ACL; атомарное завершение не включено |
| COMMIT/ROLLBACK после SET LOCAL | Контекст не сохраняется на следующий запрос при отсутствии session-level SET |
| Повторное получение того же соединения из пула: engineer → manager → без контекста | Нет полномочий предыдущего пользователя, в том числе после исключения/отмены |
| Повтор миграции | Отказ до изменения объектов |

RLS-отказ сам по себе не является HTTP 403: SELECT/UPDATE/DELETE часто дают пустой результат. Существующая ASP.NET-авторизация остаётся обязательной; различение 403/404/409/503 реализуется и тестируется на следующем этапе. При отзыве роли в параллельной транзакции действуют правила видимости PostgreSQL; мгновенная отмена уже выполняющейся команды не обещается.

## Безопасный откат

Если установка не дошла до COMMIT, выполнить ROLLBACK: объекты миграции не должны остаться. Не менять equipment_app и не запускать schema.sql заново.

После успешной установки откат — отдельная ручная административная операция:

1. Остановить новые запросы будущего PostgreSQL-адаптера и закрыть его Npgsql-пул. До включения адаптера приложение менять не требуется.
2. Выполнить `ALTER ROLE equipment_api NOLOGIN;`. Это не завершает уже открытые соединения. Проверить pg_stat_activity и после оценки активных транзакций завершить только backend_pid с usename='equipment_api'; учесть Session pooler, который может держать backend-сессии. Не завершать equipment_app и другие сервисы.
3. Убедиться в отсутствии активных соединений и новых зависимостей от роли/helpers. Проверить, что права и объекты с момента миграции не расширялись. Сохранить рабочие данные; откат безопасности не откатывает уже выполненный CRUD.
4. Выполнить следующий блок целиком доверенным администратором. Без IF EXISTS, CASCADE и DROP OWNED: неожиданные зависимости должны остановить откат, а не удалиться автоматически.

```sql
BEGIN;
DROP POLICY em_api_equipment_delete ON public.equipment;
DROP POLICY em_api_equipment_update ON public.equipment;
DROP POLICY em_api_equipment_insert ON public.equipment;
DROP POLICY em_api_equipment_read ON public.equipment;
DROP POLICY em_api_profiles_self ON public.profiles;

REVOKE SELECT (id, roles, is_active) ON public.profiles FROM equipment_api;
REVOKE INSERT (name, inventory_number, location, status),
       UPDATE (name, inventory_number, location, status)
    ON public.equipment FROM equipment_api;
REVOKE ALL ON TABLE public.equipment FROM equipment_api;
REVOKE ALL ON SEQUENCE public.equipment_id_seq FROM equipment_api;
REVOKE EXECUTE ON FUNCTION public.trim_whitespace(text) FROM equipment_api;
REVOKE ALL ON SCHEMA public FROM equipment_api;
REVOKE ALL ON DATABASE postgres FROM equipment_api;

DROP FUNCTION equipment_api_security.has_any_role(text[]);
DROP FUNCTION equipment_api_security.current_profile_id();
DROP SCHEMA equipment_api_security;
DROP ROLE equipment_api;
COMMIT;
```

При ошибке выполнить ROLLBACK и разобрать зависимости. NOLOGIN из предыдущего шага сохраняется. Не применять DROP OWNED: оно может затронуть посторонние объекты. RLS, таблицы, данные, UNIQUE/FK, старые политики и equipment_app сохраняются.

## Статическая проверка и ограничения

Проверены порядок DDL, транзакционная установка, однократность, отсутствие пароля/SECURITY DEFINER/отключения RLS, адресаты GRANT/POLICY, column grants, собственный профиль без рекурсии, USING/WITH CHECK, сохранение UNIQUE/FK и INVOKER-триггеров, проверки PUBLIC с USAGE схем и узким исключением штатных stats views. Это анализ исходников, **не выполнение PostgreSQL и не подтверждение успешного развёртывания**. SQL-парсер сервера и интеграционные проверки ещё не запускались.

Автоматические структурные проверки текста: 5 политик, все TO equipment_api; 2 INVOKER helper-функции; баланс круглых скобок соблюдён; 8 парных dollar-delimiters; отсутствуют ALTER TABLE, замена существующих объектов и DML прикладных таблиц. Проверки скобок и токенов не заменяют синтаксический анализ PostgreSQL. git diff --check завершился кодом 0; Git сообщил только о возможной будущей нормализации LF/CRLF в уже изменённом ранее Services/FaultDataService.cs, который в этом этапе не редактировался.

Build/test приложения не запускались: C# не изменён, а 55 ранее пройденных тестов не проверяют новые политики PostgreSQL. Никаких новых тестов БД пройденными не объявляется. Созданы только эта инструкция и 006_1_equipment_api_security.sql. Пакеты и пароли не добавлялись, commit/push не выполнялись.

Основания: [PostgreSQL 17: RLS и объединение политик](https://www.postgresql.org/docs/17/ddl-rowsecurity.html), [атрибуты и автоматическая ADMIN-связь создателя](https://www.postgresql.org/docs/17/role-attributes.html), [транзакционный set_config](https://www.postgresql.org/docs/17/functions-admin.html), [Npgsql: No Reset On Close](https://www.npgsql.org/doc/connection-string-parameters).
