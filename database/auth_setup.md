# Supabase Auth + JWT для REST API EquipmentManager

> Исторический документ этапа подготовки. Описания будущих работ, числа тестов и ограничения отражают состояние того этапа. Актуальная реализация, рабочие настройки и единственный порядок установки для текущего приложения — в [backend_documentation.md](../backend_documentation.md); итог тестирования — в [backend_testing.md](../backend_testing.md). Не выполнять старые альтернативные SQL-варианты по этому документу.

> Актуальный статус: реальное соединение Npgsql с VerifyFull и официальным CA успешно; активный engineer прочитан из profiles по UUID, отсутствие INSERT/UPDATE/DELETE подтверждено. На локальном приложении GET без токена и с недопустимым токеном возвращают 401. Реальный вход и GET с Supabase JWT ожидают интерактивного запуска пользователем. Исторические сведения ниже об отсутствии User Secrets/подключения устарели; текущие факты и команды: [connection_verification.md](connection_verification.md), [check_auth_locally.ps1](check_auth_locally.ps1).

Дата: 17.10.2026. Реализован согласованный вариант: Supabase идентифицирует пользователя, ASP.NET Core проверяет JWT и получает бизнес-роли из PostgreSQL public.profiles. Оборудование, заявки и история по-прежнему хранятся в памяти.

## Текущее состояние: JWT настроен, источник профилей ещё не подключён

Пользователь подтвердил параметры проекта. В appsettings.json записаны только публичные настройки: Enabled=true, SettingsConfirmed=true, Issuer=`https://cyzanurmyswswbicjiaf.supabase.co/auth/v1`, Audience=`authenticated`, SigningAlgorithm=`ES256`. Проверка JWT включена. Неполные/неподтверждённые JWT-настройки при Enabled=true по-прежнему останавливают запуск, а не отключают проверки.

Публичный Project URL: https://cyzanurmyswswbicjiaf.supabase.co. JWKS: https://cyzanurmyswswbicjiaf.supabase.co/auth/v1/.well-known/jwks.json. Получение этого документа по HTTPS фактически выполнено: найден ключ kty=EC, crv=P-256, alg=ES256, use=sig, kid=`66f7e6d2-2cb2-4eb2-aa2e-d61b7bb135a7`. Это снимок на момент проверки, не закреплённый навсегда kid: приложение получает и обновляет ключи через ConfigurationManager.

Строка PostgreSQL в appsettings.json не добавлена. При проверке отсутствовали переменная окружения ConnectionStrings__Profiles и файл User Secrets текущего проекта. При отсутствии подключения приложение запускается и проверяет JWT; без действительного токена возвращает 401, с действительным токеном при недоступном источнике ролей возвращает 503 и не допускает операцию. Никакие временные роли или разрешающие заглушки вместо БД не включаются.

Как и раньше, REST-маршруты регистрируются только в Development. В Production/Staging возвращается 404 независимо от токена. Публикация production-API остаётся отдельным этапом после проверки настоящего проекта. Никакого разрешающего резервного режима нет.

Issuer, audience и ES256/ECC P-256 теперь подтверждены. Для HS256 реализация намеренно не включается: симметричный секрет не доступен через публичный JWKS, а перенос JWT secret в конфигурацию не согласован. Пароли, приватные ключи и пользовательские токены в чат или репозиторий передавать не нужно.

## Зависимости

После разрешения пользователя проверены страницы NuGet и восстановлены фиксированные версии:

- Microsoft.AspNetCore.Authentication.JwtBearer **9.0.13**, целевая платформа net9.0;
- Npgsql **9.0.5**, совместим с net9.0.

Они добавлены в веб-проект; новые тестовые пакеты не требуются. Совместимость подтверждена сборкой и тестами net9.0. Начальный restore в ограниченной среде завершился NU1301 из-за блокировки сокетов; повторный разрешённый restore успешно загрузил зависимости. Это не было ошибкой версии пакета. Проверка самой конфигурации Supabase/БД не выполнялась.

## JWT и доверие

Клиент передаёт `Authorization: Bearer <access_token>`. Нужен пользовательский access-токен Supabase Auth, не publishable key, не anon/service_role key и не refresh token. Собственное хранение паролей, регистрация и выдача токенов приложением не добавлены.

Проверяются подпись, точный issuer, подтверждённый audience, срок действия и наличие exp; допустимый алгоритм фиксируется настройкой ES256/RS256. ClockSkew — 30 секунд. Дополнительно проверяются UUID sub, серверный claim role=authenticated и отсутствие is_anonymous=true; анонимные Auth-сеансы не поддерживаются.

JWKS-адрес строится **из доверенного issuer**: `<issuer>/.well-known/jwks.json`. Issuer должен оканчиваться на `/auth/v1`. Загрузка только по HTTPS. Адрес не извлекается из jku/x5u входного JWT. SupabaseJwksRetriever отбирает ключи подписи совместимого алгоритма; для ES256 допускается только EC/P-256, ключи enc или без разрешённого verify при заданном key_ops отклоняются. Используется стандартный ConfigurationManager с обновлением публичных ключей и обновлением при неизвестном kid. Ротация/отзыв ключей зависит от его кэша и TTL токенов, мгновенный отзыв каждого уже выданного JWT не обещается.

Бизнес-роли из `roles`, `user_metadata`, `app_metadata` и стандартных role-claims **не используются для разрешения операций**. После криптографической проверки берётся только UUID sub и выполняется параметризованный запрос:

```sql
SELECT roles, is_active FROM public.profiles WHERE id = $1;
```

Проверка профиля выполняется на каждый HTTP-запрос, без межзапросного кэша; две политики одного запроса используют один результат. Отсутствующий/неактивный профиль, пустые или неподходящие роли дают 403. Сбой источника даёт 503 и никогда не разрешает операцию; в журнал пишется тип сбоя без строки подключения, токена и текста исключения PostgreSQL. Секреты и профиль в ответ не включаются.

## Матрица REST API

| Операция | engineer | maintenance_specialist | manager |
| --- | --- | --- | --- |
| GET /api/equipment и /api/equipment/{id} | Разрешено | Разрешено | Разрешено |
| POST /api/equipment | Разрешено | 403 | 403 |
| PUT /api/equipment/{id} | Разрешено | 403 | 403 |
| DELETE /api/equipment/{id} | Разрешено при отсутствии связей | 403 | 403 |

Для отсутствующего/неверного токена — 401 с WWW-Authenticate: Bearer. Для корректного токена без достаточных серверных прав — 403. Production/Staging — 404, поскольку endpoints отсутствуют. Ошибки CRUD 400/404/409 остаются после успешной авторизации.

Согласование engineer для PUT/DELETE относится к этому REST API. Старый проект security_policies.sql не изменён и по-прежнему не разрешает эти операции в прямом Data API. Не применять его как готовую конфигурацию нового серверного подключения.

## Отдельная серверная роль PostgreSQL

Не использовать postgres, supabase_admin, service_role, владельца таблиц или BYPASSRLS. Вход приложения и чтение профилей — разные механизмы: для Npgsql нужен отдельный database login, а не API key. Пароль этого login хранится только на сервере вне исходников. Бизнес-роли профиля не являются login-ролями PostgreSQL.

Минимальный набор для выбранной пользователем роли `equipment_app` (предыдущее примерное имя equipment_profile_reader больше не используется):

- CONNECT к нужной БД и USAGE схемы public;
- SELECT **только столбцов id, roles, is_active** таблицы profiles;
- без INSERT/UPDATE/DELETE/TRUNCATE/REFERENCES/TRIGGER/CREATE;
- без наследуемых административных ролей, владения объектами и BYPASSRLS;
- TLS с проверкой сертификата и имени сервера (`SSL Mode=VerifyFull`).

Текущая схема включает RLS, поэтому одного SELECT недостаточно. Подготовлен отдельный [create_app_role.sql](create_app_role.sql): LOGIN без пароля, ограниченные column grants и политика em_profiles_equipment_app_read только для equipment_app. Это не security_policies.sql для JWT-ролей authenticated. **Оба файла не выполнялись.** Скрипт не отключает RLS и не меняет существующие политики других ролей. При существующей equipment_app или одноимённой политике он прекращает установку, а не перезаписывает права/пароль.

`USING (true)` здесь ограничен отдельным серверным login и только разрешёнными столбцами через ACL; он не выдаёт доступ anon/authenticated. Приложение проверяет подпись JWT и подставляет проверенный sub параметром, не принимает ID для назначения ролей из тела HTTP-запроса. Database login технически может читать эти столбцы всех профилей: его нельзя выдавать пользователям или использовать для произвольного SQL. Если это неприемлемо, нужен отдельный контракт доверенного row-context/RPC и новый аудит; текущий код не подделывает JWT-контекст БД.

Перед выдачей grants проверить существование роли, наследование и эффективные права: NOINHERIT не заменяет отзыв членства, а PUBLIC может выдавать дополнительные права (например, CREATE/TEMP). Проверить pg_roles, pg_auth_members, has_schema_privilege, has_table_privilege и has_column_privilege. Если public доступен для CREATE через PUBLIC, администратор должен отдельно устранить это в рамках политики проекта. Код проверяет TLS и явно запрещённые имена административных login, но **не способен доказать минимальность прав произвольно названной роли без аудита сервера**.

Скрипт проверяет RLS и дополнительные эффективные права через PUBLIC: лишний доступ к прикладным таблицам/столбцам или CREATE в пользовательских схемах приводит к исключению и откату установки. Глобальные PUBLIC grants не отзываются автоматически, поскольку это затронуло бы другие сервисы. Это не полный аудит базы: системные каталоги, TEMP, доступные функции/RPC (особенно SECURITY DEFINER), наследование и будущие выдачи прав требуют отдельной проверки администратора. При существующих restrictive-политиках чтение может оставаться закрытым — их нельзя удалять без анализа.

Пока подтверждена только подготовка файлов: применение SQL, задание пароля и первое подключение пользователь выполнит/разрешит отдельно. Реальные права login и чтение профилей не проверены. Код источника профилей готов; без доступного источника API не предоставляет прав.

## Session pooler: параметры и порядок ручной настройки

Выбранные пользователем параметры:

| Параметр | Значение |
| --- | --- |
| Host | aws-1-eu-central-1.pooler.supabase.com |
| Port | 5432 — Session mode |
| Database | postgres |
| PostgreSQL-роль | equipment_app |
| Project ref | cyzanurmyswswbicjiaf |
| Username Npgsql / shared pooler | **equipment_app.cyzanurmyswswbicjiaf** |
| SSL Mode | VerifyFull |

Роль в SQL создаётся без суффикса: equipment_app. Суффикс `.cyzanurmyswswbicjiaf` нужен shared pooler для выбора проекта, не создавайте PostgreSQL-роль с таким полным именем. Пароль — собственный пароль equipment_app, не пароль postgres, не пароль пользователя Supabase Auth и не service_role key. Совместимость формата custom role + project ref и session port 5432 подтверждена документацией; доступность именно этого endpoint и успешная аутентификация **не проверялись соединением**. Сверьте host/region/project ref с Connect → Session pooler своего Dashboard перед подключением.

Порядок действий **после отдельного подтверждения пользователя**:

1. Администратор проверяет schema.sql, наличие profiles, включённый RLS и отсутствие ранее созданной equipment_app. Если старый пример equipment_profile_reader уже применялся, не удалять его автоматически: сначала проверить зависимости и решить вопрос отзыва старых учётных данных.
2. В SQL Editor под доверенным администратором выполнить целиком create_app_role.sql (BEGIN/COMMIT). При ошибке выполнить ROLLBACK, разобраться с конфликтом/лишними правами; не обходить проверки заменой на GRANT ALL или BYPASSRLS.
3. Новая роль имеет LOGIN, но **пароль отсутствует**: парольная аутентификация до его назначения не работает. Никакого временного общего пароля нет.
4. Задать случайный уникальный пароль вручную через доверенный PostgreSQL-клиент. Рекомендуемый способ — интерактивный psql под администратором через TLS с проверкой сертификата, затем `\password equipment_app`. Команда запросит пароль дважды без вывода самого пароля. При необходимости перед ней установить `SET password_encryption = 'scram-sha-256';`. Не вставлять `ALTER ROLE ... PASSWORD 'настоящий пароль'` в SQL Editor, сохранённый запрос, файл или командную строку: plaintext может попасть в историю/логи. `\password` — метакоманда psql, SQL Editor её не исполняет. psql в этой задаче не устанавливался и не запускался.
5. Сохранить пароль в менеджере секретов и задать строку подключения через локальные User Secrets или защищённую переменную окружения; не передавать её в чат. Не копировать полный вывод `dotnet user-secrets list` в отчёты.
6. Только после разрешения первого подключения проверить TLS/роль и SELECT разрешённых столбцов, отрицательные запросы и API с реальным пользователем. После смены строки перезапустить приложение: NpgsqlDataSource читает её при создании. При ротации пароля учитывать существующие сессии/пулы и возможный кэш shared pooler; не ослаблять TLS при ошибке аутентификации.

Ни один из этих административных/сетевых шагов в рамках подготовки не выполнялся. Не запускайте приложение с заданной реальной строкой до разрешения подключения: первый авторизованный API-запрос может открыть Npgsql-соединение.

### Проверки после разрешённого применения

Под администратором (только запросы чтения):

```sql
SELECT rolname, rolcanlogin, rolsuper, rolcreatedb, rolcreaterole,
       rolinherit, rolreplication, rolbypassrls
FROM pg_catalog.pg_roles WHERE rolname = 'equipment_app';

SELECT parent.rolname AS member_of
FROM pg_catalog.pg_auth_members m
JOIN pg_catalog.pg_roles child ON child.oid = m.member
JOIN pg_catalog.pg_roles parent ON parent.oid = m.roleid
WHERE child.rolname = 'equipment_app';

SELECT column_name,
       has_column_privilege('equipment_app', 'public.profiles', column_name, 'SELECT') AS can_select,
       has_column_privilege('equipment_app', 'public.profiles', column_name, 'UPDATE') AS can_update
FROM information_schema.columns
WHERE table_schema = 'public' AND table_name = 'profiles';

SELECT policyname, roles, cmd, qual FROM pg_catalog.pg_policies
WHERE schemaname = 'public' AND tablename = 'profiles';
```

Ожидания: LOGIN=true; административные атрибуты, INHERIT и BYPASSRLS=false; членства нет; SELECT только id/roles/is_active; UPDATE всех столбцов=false; новая SELECT-политика адресована только equipment_app. Проверить отсутствие доступа к equipment/faults/maintenance_records и просмотра display_name.

После разрешения соединиться **как equipment_app через Session pooler** и проверить:

```sql
SELECT current_user, session_user;
SELECT ssl, version, cipher FROM pg_catalog.pg_stat_ssl WHERE pid = pg_backend_pid();
SELECT id, roles, is_active FROM public.profiles LIMIT 1;
-- Выполнять отдельно; ожидается permission denied:
SELECT display_name FROM public.profiles LIMIT 1;
SELECT * FROM public.equipment LIMIT 1;
```

Не публиковать возвращённые UUID/профили. pg_stat_ssl показывает TLS соединения сервера с pooler, а проверку клиент → pooler обеспечивает Npgsql VerifyFull; не выдавать один запрос за доказательство обоих TLS-участков. Итоговый параметризованный SELECT по проверенному UUID исполняет существующий PostgresProfileStore. Отрицательный UPDATE проверять в отдельной транзакции с ROLLBACK и WHERE false, без изменения реальных профилей. Синтаксис и результаты всех этих SQL-проверок пока не подтверждены исполнением.

## Конфигурация после подтверждения

Публичные issuer/audience/алгоритм находятся в appsettings.json; это не секреты. Для строки PostgreSQL в Program создан отдельный configuration provider: **User Secrets только в Development, затем переменные окружения**. Он не включает appsettings.json, appsettings.Development.json и аргументы командной строки. JWT-настройки и строка БД передаются регистратору раздельно; строка из общей JSON-конфигурации не используется. `.env` автоматически не загружается.

В csproj добавлен несекретный UserSecretsId=`EquipmentManager-SupabaseAuth`. Для локальной настройки можно открыть Manage User Secrets веб-проекта в Visual Studio и сохранить вне репозитория структуру:

```json
{
  "ConnectionStrings": {
    "Profiles": "Host=aws-1-eu-central-1.pooler.supabase.com;Port=5432;Database=postgres;Username=equipment_app.cyzanurmyswswbicjiaf;Password=<PASSWORD_EQUIPMENT_APP>;SSL Mode=VerifyFull;Timeout=5;Command Timeout=5;Maximum Pool Size=5;Include Error Detail=false;Persist Security Info=false"
  }
}
```

Точный ключ C# — **ConnectionStrings:Profiles**, JSON-путь — ConnectionStrings → Profiles, переменная окружения — **ConnectionStrings__Profiles**. Это соответствует GetConnectionString("Profiles"). Значение PASSWORD_EQUIPMENT_APP — только маркер, не временный пароль. При спецсимволах `;` или кавычках значение Password нужно корректно экранировать по правилам Npgsql и затем JSON; не склеивать непроверенные фрагменты. Лучше подготовить строку в доверенном локальном редакторе/через NpgsqlConnectionStringBuilder, не выводя её в консоль.

Шаблон не является рабочей строкой. User Secrets — локальное хранилище вне проекта, **не зашифрованное хранилище**: права доступа к файлу остаются важны. Альтернатива — защищённая переменная окружения ConnectionStrings__Profiles процесса приложения, она имеет приоритет над User Secrets. Не публикуйте строку в терминале, чатах и журнале команд. В Production User Secrets этим кодом не загружаются; REST-маршруты там всё равно отсутствуют. Для Session mode Npgsql поддерживается обычным PostgreSQL-протоколом; PgBouncer/transaction-mode обходы и отключение TLS не требуются. При необходимости Root Certificate указывает на доверенный сертификат, полученный проверенным способом, не на произвольный файл.

| Переменная окружения | Значение после проверки |
| --- | --- |
| SupabaseAuth__Issuer | Подтверждено: `https://cyzanurmyswswbicjiaf.supabase.co/auth/v1` |
| SupabaseAuth__Audience | Подтверждено: `authenticated` |
| SupabaseAuth__SigningAlgorithm | Подтверждено: `ES256`, кривая P-256 |
| SupabaseAuth__SettingsConfirmed | true только после подтверждения всех трёх значений |
| SupabaseAuth__Enabled | Сейчас true; без источника ролей доступ к операциям всё равно закрыт |
| ConnectionStrings__Profiles | Секретная серверная строка подключения отдельной роли |

Строка подключения должна включать корректные host/database/username/password, `SSL Mode=VerifyFull`; при необходимости доверенный корневой сертификат задаётся на сервере. Не отключать проверку сертификата. Код отвергает известные административные login, Include Error Detail=true и Persist Security Info=true. Для пула Supabase используйте фактический username/endpoint из Dashboard для отдельной роли и проверьте режим подключения, не копируйте пароль в документы. Создание NpgsqlDataSource ленивое; в выключенном режиме БД не открывается.

Для намеренного полного отключения JWT можно задать Enabled=false: REST в Development будет возвращать 401. Подтверждённая текущая конфигурация включает JWT, без фиктивных полномочий. Команды запуска:

```powershell
dotnet build EquipmentManager.sln
dotnet test EquipmentManager.sln --no-restore
dotnet run --project EquipmentManager.csproj --launch-profile http
```

Профиль http подходит только для localhost. За пределами локальных тестов токены должны передаваться по HTTPS. Не записывать Authorization в логи, историю команд, URL или HAR. Не хранить токен пользователя в singleton-клиенте Blazor.

## Подготовка тестовых пользователей Supabase

1. Через Authentication в Dashboard создать отдельные тестовые учётные записи выбранным способом входа; не добавлять пароли напрямую в profiles/auth.users SQL.
2. Доверенный администратор создаёт profiles с UUID соответствующих auth.users. Назначить одному пользователю engineer, другому maintenance_specialist, третьему manager. Для отрицательных проверок подготовить неактивный профиль и профиль без ролей. Пользователю нельзя давать возможность менять roles/is_active самостоятельно.
3. В Authentication → Signing Keys проверить фактический алгоритм и наличие публичного ключа в JWKS. Проверить issuer/audience локально по настройкам/собственному access-токену без отправки токена сторонним декодерам; чтение payload без проверки подписи само по себе не аутентификация.
4. После подтверждения конфигурации войти через Supabase Auth и передавать access-токен в заголовке Authorization. REST API не содержит собственного login/password endpoint.
5. Проверить 401 без токена, 403 manager на POST/PUT/DELETE, успешное чтение для трёх ролей, успешный CRUD engineer над отдельной несвязанной записью. Затем отозвать роль/выключить профиль и проверить отказ на следующем запросе.

Эти облачные действия не выполнялись. Автоматическое получение токена через реальный Auth, TLS к PostgreSQL и действительные профили требуют отдельной проверки после настройки.

## Удаление, Blazor и границы изменений

REST DELETE теперь использует FaultDataService.DeleteEquipment: под общей с регистрацией заявки блокировкой проверяет заявки и историю, возвращает 409 при связях и 204 только для несвязанной записи. Связанные данные не удаляются; повтор отсутствующего ID — 404. Оборудование и история не перенесены в PostgreSQL, FK самой БД при REST CRUD пока не вызываются.

Страницы Blazor и их текущие обработчики не переведены на REST и не получили новую авторизацию. Их прежний EquipmentDataService.Delete сохранён для совместимости; проверка связей этим шагом добавлена именно в API. Следовательно, это не защита всего UI и не готовность всего приложения к публичному production-размещению. Прежние тесты компонентов и сервисов сохранены; HTTP-тесты CRUD теперь используют настоящие подписанные тестовые JWT и инженерный профиль.

## Автоматические проверки и фактические результаты

Тесты используют настоящий JwtBearer, ES256/ECC P-256, подтверждённые issuer/audience и рабочий SupabaseJwksRetriever. Тестовый IDocumentRetriever возвращает локальный JWKS с публичными координатами сгенерированного тестового ключа. Это не ключ реального проекта и не токен реального пользователя. Приватный EC-ключ генерируется во время теста и не записывается в репозиторий. Сеть и PostgreSQL IProfileStore изолированы тестовым DI; рабочего издателя JWT или разрешающей заглушки в приложении нет. В отдельной проверке настоящий PostgreSQL provider без строки подключения возвращает 503 без обращения к сети, несмотря на строку в общей тестовой конфигурации.

Проверены все пять 401-маршрутов; 403 для manager/maintenance_specialist на запись; чтение для трёх ролей; игнорирование ложных engineer-claims; неверные issuer, audience, срок, подпись и HS256; отсутствие exp и будущий not-before; inactive/missing/empty profiles; 503 при недоступном источнике или отсутствии защищённой строки; запрет удаления со связями и с завершённой историей; блокировка неполной активации; прежний CRUD и отсутствие маршрутов в Production/Staging. Добавлены проверки фильтрации JWKS по кривой, use/alg и включённых параметров подписи/issuer/audience/lifetime.

Выполнены после всех изменений кода:

- `dotnet build EquipmentManager.sln`: код 0, **0 ошибок, 0 предупреждений**.
- `dotnet test EquipmentManager.sln --no-build --no-restore`: код 0, **найдено 55, пройдено 55, провалено 0, пропущено 0**.

Дополнительно запущен настоящий Program с подтверждённой конфигурацией и без строки БД: GET /api/equipment без токена — 401, с Bearer invalid-token — 401, GET /equipment — 200. Публичный JWKS фактически получен по HTTPS; его содержимое согласуется с ES256/P-256. Ротация реальных ключей не проверялась.

Первый сетевой запрос к JWKS был заблокирован ограничениями среды; разрешённый повтор прошёл. Во время промежуточного тестового прогона запущена перекрывающая его сборка, что вызвало MSB3026 из-за блокировки testhost. Перекрывающая операция остановлена, предыдущий прогон завершён, затем итоговые build/test выполнены последовательно без ошибок и предупреждений. Это не ошибка JWT.

**Система не считается полностью проверенной:** реальный вход Supabase-пользователя и валидация подписи его access-токена через настоящий JWKS не выполнялись. Нет строки подключения отдельной роли; права login, SELECT/RLS-доступ к profiles и возврат настоящих roles/is_active не проверены. Требуется локально задать строку через разрешённый источник, подготовить/проверить минимальные права роли отдельным администратором и пройти сценарии реальных 401/403/разрешённого CRUD после входа. Пароли и токены в отчёт/репозиторий не добавлять. SQL и security_policies.sql не выполнялись. Commit/push не выполнялись.

## Изменённые файлы

Этап подготовки Session pooler: создан только database/create_app_role.sql и обновлён database/auth_setup.md. Приложение, реальные User Secrets и настройки Supabase не изменялись. SQL/подключения/build/test в этом документальном этапе не запускались; ранее указанные 55 тестов относятся к предыдущей настройке JWT, а не проверке роли. Commit/push не выполнялись.

Текущий этап настройки подтверждённого проекта: EquipmentManager.csproj, Program.cs, appsettings.json, Services/Authentication/SupabaseAuthentication.cs, ProfileStore.cs, новый SupabaseJwksRetriever.cs, EquipmentManager.Tests/ApiAuthFixture.cs, EquipmentAuthorizationTests.cs, новый SupabaseJwksTests.cs и database/auth_setup.md. Ниже сохранён перечень предыдущего этапа для контекста:

- EquipmentManager.csproj — разрешённые зависимости.
- Program.cs, appsettings.json — middleware и выключенная до подтверждения конфигурация.
- Services/Authentication/SupabaseAuthSettings.cs, SupabaseAuthentication.cs, ProfileStore.cs, ProfileAuthorization.cs — JWT, PostgreSQL и политики.
- Services/EquipmentApiEndpoints.cs — обязательные политики REST.
- Services/FaultDataService.cs, Models/EquipmentSaveResult.cs — результат удаления с проверкой связей.
- EquipmentManager.Tests/EquipmentApiTests.cs, ApiAuthFixture.cs, EquipmentAuthorizationTests.cs — обновлённые и новые проверки.
- database/auth_setup.md, database/api_design.md, database/security_description.md — актуальные инструкции и границы реализации.

## Источники

- [Supabase: custom-role username в shared pooler](https://supabase.com/docs/guides/troubleshooting/tenant-or-user-not-found).
- [Supabase: Session pooler, порт 5432](https://supabase.com/docs/guides/troubleshooting/supavisor-faq-YyP5tI).
- [PostgreSQL: безопасная смена пароля через psql](https://www.postgresql.org/docs/18/sql-alterrole.html).

- [Supabase JWT Signing Keys и JWKS](https://supabase.com/docs/guides/auth/signing-keys).
- [Microsoft: JwtBearer](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/configure-jwt-bearer-authentication?view=aspnetcore-9.0).
- [JwtBearer 9.0.13: совместимость](https://www.nuget.org/packages/Microsoft.AspNetCore.Authentication.JwtBearer/9.0.13).
- [Npgsql 9.0.5: совместимость](https://www.nuget.org/packages/Npgsql/9.0.5).
