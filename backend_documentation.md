# Лабораторная №5: Backend EquipmentManager

## Состояние и архитектура

Браузер → Blazor InteractiveServer (SignalR) → серверный `SessionBackendClient` → HTTPS REST API ASP.NET Core → `ApplicationDataService` → выбранный комплект хранилищ → PostgreSQL или память.

В PostgreSQL используются `PostgresEquipmentStore`, `PostgresFaultStore`, `PostgresMaintenanceStore` и единый транзакционный `PostgresStoreExecutor`. Все операции выполняются под ограниченным логином `equipment_api`. Отдельный `PostgresProfileStore` читает назначения ролей через `ConnectionStrings:Profiles` (`equipment_app`); это подключение не используется для CRUD.

Frontend не обращается к PostgreSQL или Supabase Data API из браузера. `SessionBackendClient` добавляет пользовательский Bearer JWT на каждый серверный HTTP-запрос. Новые разрешения CORS для этого пути не нужны. Cookie сама по себе не авторизует REST API. Профили проверяются на сервере, а не по бизнес-ролям из user_metadata/JWT.

По сообщению пользователя, основная EquipmentManager настроена и полный сценарий вручную работает. В общем appsettings оставлен InMemory; реальный PostgreSQL-режим включается локальным переопределением. Последний локальный прогон: 140 обычных тестов, сборка без предупреждений и ошибок. Отдельный облачный интеграционный тест не запускался. См. [протокол шага 8](backend_testing.md).

## Настройка и запуск

Открыть решение в Visual Studio, выбрать веб-проект и профиль **https**, доверить сертификат и запустить F5. Адрес профиля — `https://localhost:7128/`. Параметр `SupabaseUiAuth:BackendBaseUrl` должен указывать на HTTPS origin своего Backend с завершающим `/`; после смены порта изменить его вручную. Значение не вычисляется из пользовательского Host.

| Параметр | Источник и назначение |
| --- | --- |
| `Storage:Provider` | InMemory или PostgreSQL; общий appsettings, User Secrets либо `Storage__Provider` |
| `SupabaseAuth:Enabled`, `SettingsConfirmed` | Явное включение подтверждённых параметров JWT |
| `SupabaseAuth:Issuer` | `https://<project-ref>.supabase.co/auth/v1`, без завершающего `/` |
| `SupabaseAuth:Audience` | Для текущей конфигурации `authenticated` |
| `SupabaseAuth:SigningAlgorithm` | Для текущей конфигурации `ES256`; должен совпадать с проектом |
| `SupabaseUiAuth:BackendBaseUrl` | Доверенный HTTPS-адрес ASP.NET приложения |
| `ConnectionStrings:Profiles` | Только User Secrets/окружение, ограниченный читатель профилей |
| `ConnectionStrings:EquipmentCrud` | Только User Secrets/окружение, логин `equipment_api`; нужен для PostgreSQL |
| `SupabaseUiAuth:PublishableKey` | Только User Secrets/окружение, publishable либо допустимый legacy anon key того же Supabase |

User Secrets автоматически читаются в Development. На сервере секреты передаются через переменные окружения `ConnectionStrings__Profiles`, `ConnectionStrings__EquipmentCrud`, `SupabaseUiAuth__PublishableKey`. Не писать значения в appsettings, исходники или отчёт. `.env`/локальные JSON-файлы автоматически не загружаются. Секретные `service_role`/`sb_secret_...` для приложения не нужны и не допускаются клиентом Auth.

Для CRUD взять host/port из своего Dashboard, Database `postgres`, Username `equipment_api` (для Supavisor — `equipment_api.<project-ref>`), пароль именно этого логина, `SSL Mode=VerifyFull`. Для профилей — отдельный `equipment_app` с тем же требованием TLS. Не включать `Include Error Detail`/`Persist Security Info`; CRUD также запрещает `No Reset On Close` и Multiplexing. Не заменять имя TLS-хоста IP-адресом. Доверие к сертификату БД настраивается по инструкции провайдера.

`Storage:Provider` выбирает весь комплект при старте. InMemory содержит демонстрационные записи и теряет изменения при остановке; PostgreSQL сохраняет данные и не переходит к памяти при ошибке. Неверный режим или неподходящая CRUD-конфигурация останавливают запуск. Доступность БД проверяется при запросе, не при регистрации DI. После изменения режима нужен перезапуск. Нет автоматической установки SQL или переноса демонстрационных данных.

Для нового стенда нужны Auth-пользователи и соответствующие `profiles.id = auth.users.id`, `is_active=true`, роли по матрице ниже. Управление учётными записями и назначениями выполняется администратором вне приложения; отдельного API регистрации пользователей нет.

Для размещения вне Visual Studio потребуется ASP.NET Core-хост с .NET 9, HTTPS и поддержкой WebSocket/SignalR, переменные окружения для секретов и собственный BackendBaseUrl. User Secrets и launchSettings на хост не переносятся. Публикация выполняется средствами Visual Studio либо `dotnet publish EquipmentManager.csproj -c Release`; настройка конкретного хостинга и фактическое внешнее развёртывание не выполнялись. Текущая реализация сеансов рассчитана на один процесс.

## Порядок установки SQL

Это инструкция для **нового проекта**, не команда повторной установки действующей основной базы. По сообщению пользователя, 006_1 и 006_3 уже установлены в основной EquipmentManager. Скрипты однократные; существующие объекты не удалять для повторного запуска.

1. В нужном Supabase-проекте с PostgreSQL 17+ проверить исходные условия из [supabase_setup.md](database/supabase_setup.md). Выполнить весь [schema.sql](database/schema.sql): четыре таблицы, ограничения, индексы и триггеры. `auth.users` предоставляется Supabase Auth.
2. Выполнить весь [create_app_role.sql](database/create_app_role.sql): ограниченный читатель профилей `equipment_app`. Он необходим также как предусловие 006_1. Пароль логина задаётся отдельно скрытым интерактивным вводом, не SQL-литералом в Git.
3. Выполнить весь [006_1_equipment_api_security.sql](database/006_1_equipment_api_security.sql): `equipment_api`, helper-функции, права оборудования и чтения своего профиля. Учесть preflight и точное инфраструктурное исключение из [инструкции 6.1](database/equipment_api_setup.md). Функцию Supabase `rls_auto_enable()` и её PUBLIC EXECUTE не менять.
4. Строго после 006_1 выполнить весь [006_3_faults_maintenance_security.sql](database/006_3_faults_maintenance_security.sql): права и RLS неисправностей/истории, включая права существующего триггера. Отдельно задать пароль `equipment_api` и локальные настройки приложения.

Каждый из этих SQL-файлов содержит BEGIN/COMMIT; запускать целиком доверенным администратором. При ошибке не продолжать фрагментами; незавершённую ошибочную транзакцию откатить и разобраться с предусловиями. Не выполнять повторно уже установленные скрипты. Установка в EquipmentManager-Test не устанавливает их в основной базе.

**Не включать в эту последовательность:** `security_policies.sql` — альтернативный вариант прямого Data API с authenticated/auth.uid(), не используемый серверным executor; `006_1_rls_auto_enable_revoke.sql` — исторический отклонённый вариант, не выполнять; diagnostics/readonly-файлы — отдельные проверки, не миграции приложения. Скрипты не выполнялись при оформлении документации.

## Авторизация и целостность

| Возможность | engineer | maintenance_specialist | manager |
| --- | --- | --- | --- |
| Читать оборудование, заявки и историю | Да | Да | Да |
| Создавать/изменять/удалять оборудование | Да | Нет | Нет |
| Регистрировать неисправность | Да | Да | Нет |
| Начинать и завершать работы | Нет | Да | Нет |

Supabase Auth проверяет email/пароль. Backend проверяет подпись JWT, issuer, audience, срок и допустимый алгоритм. Интерфейс использует Secure/HttpOnly cookie и antiforgery для форм входа/выхода. Access token хранится только в памяти сервера, не в cookie/localStorage/логах. Refresh token не сохраняется, автоматического refresh нет; при истечении требуется повторный вход. Перезапуск завершает локальные сеансы. Решение рассчитано на один процесс приложения; многосерверное хранение сеансов не реализовано.

В каждой CRUD-транзакции проверяется фактический `equipment_api`, устанавливается локальный `equipment.user_id` из проверенного `HttpContext.User.sub`, проверяются активный профиль и роли; затем действуют RLS и SQL-ограничения. UUID автора из входного JSON не используется. Заявка проходит только последовательные переходы с `expectedVersion`. Завершение обновляет заявку, существующий AFTER-триггер создаёт одну историю в той же транзакции; ошибка триггера отменяет операцию. Отдельного API записи истории нет. Удаление связанного оборудования запрещено. Автор заявки отображается строкой UUID; показ имени автора не реализован.

## Все REST endpoints

Все 12 маршрутов требуют Bearer JWT и подходящий активный профиль; действуют во всех средах приложения. JSON использует camelCase.

| Метод и путь | Запрос | Успех | Доступ |
| --- | --- | --- | --- |
| GET `/api/equipment?query=...` | Необязательный поиск | 200, Equipment[] | Три роли |
| GET `/api/equipment/{id}` | ID | 200, Equipment | Три роли |
| POST `/api/equipment` | Поля Equipment без id | 201, Equipment + Location | engineer |
| PUT `/api/equipment/{id}` | Те же поля | 200, Equipment | engineer |
| DELETE `/api/equipment/{id}` | ID | 204, без тела | engineer |
| GET `/api/faults` | Без тела | 200, Fault[] | Три роли |
| GET `/api/faults/{id}` | ID | 200, Fault | Три роли |
| POST `/api/faults` | equipmentId, description | 201, Fault + Location | engineer, maintenance_specialist |
| POST `/api/faults/{id}/start` | expectedVersion | 200, Fault | maintenance_specialist |
| POST `/api/faults/{id}/complete` | expectedVersion, workDescription, result | 200, Fault | maintenance_specialist |
| GET `/api/maintenance-records?equipmentId=...` | Необязательный ID оборудования | 200, MaintenanceRecord[] | Три роли |
| GET `/api/session/permissions` | Без тела | 200, ApplicationPermissions | Три роли |

### Примеры HTTP-запросов и ответов

Данные ниже вымышленные. `<access-token>` — placeholder пользовательского access token, не service_role. Для всех `/api` запросов нужен `Authorization: Bearer <access-token>`; JSON-запросам также `Content-Type: application/json`. Примеры предназначены для чтения; реальные токены не сохранять в файлы, скриншоты или историю команд.

```http
POST /api/equipment HTTP/1.1
Host: localhost:7128
Authorization: Bearer <access-token>
Content-Type: application/json

{"name":"Насос","inventoryNumber":"LAB-001","location":"Цех 1","status":"Исправно"}

HTTP/1.1 201 Created
Location: /api/equipment/123
Content-Type: application/json

{"id":123,"name":"Насос","inventoryNumber":"LAB-001","location":"Цех 1","status":"Исправно"}
```

`GET /api/equipment/123` возвращает 200 с таким же объектом; `GET /api/equipment?query=LAB-001` — 200 с массивом этих объектов (либо `[]`). Поиск выполняется по имени или номеру. `PUT /api/equipment/123` с теми же четырьмя полями, например новым location, возвращает 200 с обновлённым объектом. `DELETE /api/equipment/123` для записи без связей возвращает 204 без тела; после регистрации заявки этот DELETE вернёт 409.

```http
POST /api/faults HTTP/1.1
Host: localhost:7128
Authorization: Bearer <access-token>
Content-Type: application/json

{"equipmentId":123,"description":"Повышенная вибрация"}

HTTP/1.1 201 Created
Location: /api/faults/45
Content-Type: application/json

{"id":45,"equipmentId":123,"equipmentLabel":"Насос (LAB-001)","description":"Повышенная вибрация","status":"Новая","author":"11111111-1111-4111-8111-111111111111","createdAt":"2026-10-17T10:00:00+00:00","startedAt":null,"completedAt":null,"workDescription":"","result":"","version":1}
```

`GET /api/faults/45` возвращает 200 с объектом Fault; `GET /api/faults` — 200 с массивом Fault. Даты, ID и версии берутся из ответа, не назначаются клиентом.

```http
POST /api/faults/45/start HTTP/1.1
Host: localhost:7128
Authorization: Bearer <access-token>
Content-Type: application/json

{"expectedVersion":1}

HTTP/1.1 200 OK
```

Тело — полный Fault: status `В работе`, version `2`, заполнен startedAt. Для команды требуется пользователь-специалист.

```http
POST /api/faults/45/complete HTTP/1.1
Host: localhost:7128
Authorization: Bearer <access-token>
Content-Type: application/json

{"expectedVersion":2,"workDescription":"Заменён подшипник","result":"Вибрация устранена"}

HTTP/1.1 200 OK
```

Тело — полный Fault: status `Завершена`, version `3`, заполнен completedAt, сохранены workDescription/result. Числа версий иллюстрируют обычную последовательность; всегда передавать последнюю полученную версию.

```http
GET /api/maintenance-records?equipmentId=123 HTTP/1.1
Host: localhost:7128
Authorization: Bearer <access-token>

HTTP/1.1 200 OK
Content-Type: application/json

[{"faultId":45,"equipmentId":123,"equipmentLabel":"Насос (LAB-001)","performedAt":"2026-10-17T10:10:00+00:00","workDescription":"Заменён подшипник","result":"Вибрация устранена"}]
```

Без equipmentId возвращается вся доступная история. В модели истории ключ — faultId, отдельного id нет.

```http
GET /api/session/permissions HTTP/1.1
Host: localhost:7128
Authorization: Bearer <access-token>

HTTP/1.1 200 OK
Content-Type: application/json

{"canManageEquipment":true,"canRegisterFault":true,"canProcessFaults":false}
```

Последний ответ соответствует инженеру; Backend заново проверяет разрешение каждой команды независимо от скрытых/показанных кнопок.

### Обработчики сеанса вне REST API

`POST /account/login` и `POST /account/logout` — обычные HTTPS-формы `application/x-www-form-urlencoded`, защищённые antiforgery. Вход принимает email, password, returnUrl и сгенерированное Razor поле antiforgery; выход — поле antiforgery и cookie. Использовать встроенные формы, а не JSON. При успехе — 302 и Set-Cookie (установка при входе/удаление при выходе), переход по локальному адресу. Ошибки учётных данных перенаправляют на `/login?error=...`; нарушение antiforgery/HTTPS даёт 400. Удалённый logout вызывается с scope=local; при сетевой ошибке локальный выход сохраняется и показывается предупреждение. Уже выданный JWT может действовать до exp. GET `/login` и `/account/session` — страницы Blazor, не JSON API.

## Ошибки и диагностика

| HTTP | Значение |
| --- | --- |
| 400 | Валидация полей, неверный переход/ID/версия команды, некорректная форма |
| 401 | Нет действительного JWT; клиент отзывает локальный сеанс и предлагает вход |
| 403 | Недостаточно прав активного профиля; действующий сеанс сохраняется |
| 404 | Запись не найдена |
| 409 | Дубликат номера, конфликт версии или запрет удаления связанных записей |
| 500 | Ошибка БД или неожиданное исключение команды API, без раскрытия внутренних данных |
| 503 | Источник профилей/данных недоступен либо результат COMMIT неизвестен |

Пример прикладного отказа:

```http
HTTP/1.1 409 Conflict
Content-Type: application/problem+json

{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.10","title":"Операция не выполнена.","status":409,"code":"inventory_number_conflict","traceId":"example-trace-id","errors":{"operation":["Оборудование с таким инвентарным номером уже существует."]}}
```

`type` может определяться версией ASP.NET Core; для обработки использовать status/code/errors. Инфраструктурные 401/403 могут быть без JSON; 503 источника профилей содержит title и traceId, без errors.operation. Клиент также показывает понятную ошибку сети/нечитаемого ответа; это локальная обработка, не выдуманный ответ сервера.

Формы сохраняют черновик при отказе. Если подтверждение записи потеряно, нужно обновить список перед повтором; автоматических повторов записи нет. Серверный журнал содержит тип ошибки, этап работы executor, SQLSTATE, HTTP-код и TraceId, без паролей, токенов, параметров SQL и исходного текста исключения. Неожиданные исключения внутри API обрабатывает endpoint filter; вне него действует стандартный конвейер ASP.NET Core. Подробности и ручные проверки — [шаг 7](error_handling_logging.md). Ошибки прямого Npgsql искать в Supabase Postgres Logs по времени/SQLSTATE; локальный TraceId туда автоматически не передаётся.

## Проверка результата и работа с ИИ

Основной сценарий вручную подтверждён пользователем: вход/выход, создание оборудования, регистрация инженером, начало и завершение специалистом, история, валидация, дубликаты и роли. Сохранность после перезапуска проверяется отдельно: остановить приложение, запустить снова, повторно войти и прочитать те же записи; настоящий автоматический тест повторного подключения пока не запускался.

ИИ использован поэтапно для анализа требований и существующей архитектуры, подготовки PostgreSQL-хранилищ и миграций, подключения DI/API/страниц, проверки ошибок и написания локальных тестов. Результаты проверялись сборкой, 140 обычными тестами и отдельными ручными действиями пользователя. Новые архитектурные механизмы в шагах 7–9 не добавлялись. Документ не является заявлением о выполнении облачного теста или о развёртывании приложения на внешнем хостинге.

## Проверка перед GitHub — шаг 9

17.10.2026 проверены текущие отслеживаемые и неигнорируемые файлы-кандидаты для Git: настройки, документация, исходники и тесты. Реальные JWT, приватные ключи, секретные API-ключи, пароли и строки подключения с действующими секретами не обнаружены. Совпадения проверенных шаблонов относятся к переменным, placeholders и явно искусственному ключу тестовой подмены. Публичные URL/project-ref Supabase, UserSecretsId и имена PostgreSQL-ролей не являются паролями. Дополнительно проверены три доступных коммита по шаблонам JWT, приватных ключей и секретных ключей; совпадений нет. Это проверка по характерным форматам, а не гарантия обнаружения любого возможного секрета.

`git ls-files -ci --exclude-standard` не выявил уже отслеживаемых игнорируемых файлов. Проверено исключение secrets.json/каталогов Secrets, локальных appsettings, .env, пользовательских HTTP-настроек, bin/obj, TestResults, отдельных trx/coverage-отчётов и дампов. Фактическое содержимое внешних User Secrets не открывалось и не изменялось. Общие appsettings/launchSettings не содержат учётных данных. Локальные ссылки обновлённой документации проверены; ошибок пробелов в diff нет.

По результатам проверки файлы подготовлены к публикации после просмотра пользователем. Источники C#, SQL-миграции и Supabase в шаге 9 не менялись; сборка/тесты повторно не запускались, использован подтверждённый результат шага 8. Добавление файлов в индекс, commit и push не выполнялись.
