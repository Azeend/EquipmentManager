# ДЗ6: структурированные JSON-логи

Используются только Microsoft.Extensions.Logging и встроенный **JSON Console Formatter**.
Новые NuGet-пакеты, файловые журналы, внешние сервисы и облачные настройки не добавляются.
После публикации существующего `ASPNETCORE_ENVIRONMENT=Production` достаточно; новых
переменных Render не требуется. В Development формат тоже JSON, но строгий список категорий
применяется только в Production.

## Формат и просмотр в Render

Открыть существующий Web Service → **Logs**. Каждая прикладная запись — одна JSON-строка.
Render может добавлять собственные время/оформление вокруг сообщения. Сообщения платформы,
Docker build и инструмента dotnet не являются прикладными JSON-логами.

Поля встроенного формата:

- `Timestamp` — UTC, например `2026-10-17T12:00:00.000Z`;
- `LogLevel` — Information, Warning, Error или Critical;
- `Category` — источник сообщения (имя ILogger);
- `EventId` — числовой идентификатор;
- `Message` — читаемый текст;
- `State` — структурированные поля, включая `Event`, этап, категорию отказа и тип ошибки.

Information/Warning выводятся в stdout, Error/Critical — в stderr; Render показывает оба потока.
Встроенный формат может повторять текст внутри State: это поля одной записи, а не второй лог.
Не передавать предварительно сериализованный JSON в ILogger — сериализацию выполняет formatter.

Пример безопасной записи (показаны основные поля):

```json
{"Timestamp":"2026-10-17T12:00:00.000Z","EventId":3001,"LogLevel":"Error","Category":"EquipmentManager.Services.Authentication.PostgresProfileStore","Message":"Источник ролей недоступен","State":{"Event":"ProfileStoreUnavailable","Stage":"open_connection","Category":"tls_or_certificate","ErrorType":"NpgsqlException"}}
```

В Render искать ASCII-имя события, например `ProfileStoreUnavailable` или `HealthStateChanged`.
JSON-форматтер может экранировать русский текст как `\uXXXX`; это корректный JSON.
Не отправлять реальные неверные пароли и не ломать production-подключения специально для получения логов.

## Основные события

| EventId | State.Event | Значение |
|---|---|---|
| 1000 | ApplicationStarted | Процесс завершил запуск; это не подтверждение доступности БД |
| 1001 | UnhandledRequestError | Необработанный HTTP-сбой; только вид запроса, тип исключения и диагностический TraceId |
| 1002 | ApplicationFailed | Фатальный сбой запуска/выполнения хоста; Critical, процесс завершается с кодом 1 |
| 2001 | SupabaseSignInRejected | Отказ password/PKCE-входа: Stage, AuthStatus, Category, ErrorType |
| 2002 | AuthorizationDenied | Challenge/forbidden существующей политики авторизации |
| 2003 | InvalidSignInForm | Некорректные размеры/обязательные поля формы, без значений полей |
| 2004 | UiProfileAccessDenied | Нет активного профиля с допустимыми ролями; без email/UUID/списка ролей |
| 3001 | ProfileStoreUnavailable | Ошибка источника профилей, включая TLS/DNS/timeout |
| 3002 | PostgresOperationRejected | PostgreSQL отклонил операцию; только Stage и SQLSTATE |
| 3003 | PostgresConnectionFailed | Сбой хранилища; Stage, ErrorType и CommitStarted |
| 6001 | HealthStateChanged | Первое наблюдение или смена состояния проверки |

Существующие безопасные записи API, Яндекс OAuth/Userinfo, страницы истории и ошибки аналитики
сохранены в их прежних категориях; некоторые имеют EventId=0. У API остаются HTTP Status,
Code, SQLSTATE и серверный TraceId. Корневой `Category` JSON и `State.Category` — разные поля:
первое обозначает ILogger, второе — категорию отказа.

Для `SupabaseSignInRejected`: HTTP 400/401/403 + `auth_rejected` означает отклонение Auth,
но само по себе не доказывает неверный пароль; `http_transport`/`timeout` — связь,
`jwt_validation` — проверка JWT. Тела ответов Supabase, токены и учётные данные не записываются.

Health Checks сохраняют прежний EventId=6001, кеш и тайм-ауты. Успешный ответ
`/health/ready` не создаёт HTTP access log. После первого Healthy, пока состояние не меняется,
повторные обращения UptimeRobot каждые пять минут не создают новых HealthStateChanged.
Приложение не настраивает и не меняет UptimeRobot.

## Защита Production и объём

JSON-формат сам по себе **не редактирует секреты**. Защита состоит из двух частей:

1. Проверенные события приложения используют только фиксированные имена/категории,
   числовые статусы, SQLSTATE, типы исключений и серверный диагностический TraceId.
   Не передаются Exception/Message/StackTrace, строки подключения, пароли, JWT, OAuth-коды,
   cookies, заголовки, email, UUID пользователя, claims, формы, URL/query и исходные ответы сервисов.
2. В Production разрешён точный список проверенных категорий в `StructuredLogging.cs`.
   Произвольные сообщения Microsoft.AspNetCore, IdentityModel, Npgsql, HttpClient и новые
   непроверенные категории исключены на всех уровнях. Scopes выключены. Конфигурация
   `Logging__...` не может повторно включить scopes или чувствительные категории в Production:
   безопасные настройки задаются через PostConfigure.

Оставлен один Console provider: дубликаты стандартных Debug/EventLog/Console providers удалены.
Нет записи каждого HTTP-запроса, успешного входа или каждого SQL-запроса. Отказы авторизации
журналируются по попытке, состояния Health Checks — только при изменении. Ошибка БД и отказ API
могут быть отдельными связанными событиями: первое описывает источник, второе — результат операции.

Добавляя новую категорию/сообщение приложения, проверить все поля до включения в список.
Нельзя логировать секреты даже из разрешённой категории: formatter не является универсальным
редактором. Не вызывать Console.WriteLine с чувствительными данными в обход ILogger.

## Влияние и ограничения

Авторизация, API, cookie-сеансы, JWT, роли PostgreSQL, SQL, тайм-ауты Health Checks и облачные
настройки сохранены. Middleware безопасной ошибки повторно выбрасывает то же исключение;
существующий UseExceptionHandler продолжает определять HTTP-ответ.

Формат, категории и детализация логов меняются после публикации. Raw stack traces и подробные
сообщения фреймворка в Production недоступны, включая circuit/transport-диагностику Blazor.
Необработанные HTTP-ошибки фиксируются безопасным событием, но это не перехват всех фоновых
ошибок circuit. Для глубокого расследования использовать локальное воспроизведение/отладчик,
не включая чувствительные production-логи. При фатальном сбое вместо необработанного вывода
runtime записывается безопасный Critical, а приложение завершается с ненулевым кодом.
При работе без ошибок дополнительная нагрузка невелика: одно событие запуска и прежние
события диагностики; массовые неуспешные попытки входа по-прежнему могут создавать много Warning.

## Проверки

```text
dotnet build EquipmentManager.sln --configuration Release --no-restore
dotnet test EquipmentManager.sln --configuration Release --no-build --no-restore --filter "Category!=PostgresIntegration"
node --test EquipmentManager.Tests/metrika.test.mjs
dotnet format EquipmentManager.sln --verify-no-changes --no-restore
```

Тесты захватывают реальные stdout/stderr встроенного Console provider, проверяют однострочный
JSON, уровни, поля, отключение scopes/непроверенных категорий, безопасные исключения, события
запуска/отказов и отсутствие повторов Health Checks после истечения кеша. Запросы идут только
к изолированному локальному HTTP-хосту; транспорт Auth, JWKS и пробы PostgreSQL подменены.

[Официальная документация JSON Console Formatter](https://learn.microsoft.com/en-us/dotnet/core/extensions/logging/console-log-formatter).
