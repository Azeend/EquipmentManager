# Аудит безопасности EquipmentManager

Дата: 17 октября 2026 года. Лабораторная работа №6, шаг 2.

Проверены исходники и конфигурация на основе коммита `f287a5f08b95787a3d0978ff2040b8c13c876324`, прямые и транзитивные NuGet-зависимости, Dockerfile, GitHub Actions и публичный адрес https://equipment-manager-demo.onrender.com/. Критических и высоких подтверждённых проблем в пределах проверки не найдено. Подтверждён обход ограничения размера формы; он исправлен локально. Режим Render Free сохраняет согласованное доверие ingress без проверки IP. Успешная эксплуатация этого остаточного риска через публичный адрес не установлена.

Отчёт описывает проверку, а не гарантирует отсутствие всех уязвимостей. Файл ДЗ.docx/ДЗ6.docx в доступном каталоге методичек не найден; выполнены требования, перечисленные пользователем. Для сопоставления использован OWASP Top 10:2021 с соответствиями актуальному OWASP Top 10:2025.

## Область и ограничения

- Облачные действия: восемь последовательных GET-запросов без входа, реальных токенов, перебора паролей, изменения данных и агрессивного сканирования. Ответы и cookie целиком в отчёт не копировались.
- Supabase, реальные строки подключения, пароли, пользовательские профили, настройки Auth, RLS и содержимое БД не читались и не изменялись. SQL-скрипты изучены только как исходники; соответствие работающей БД этим скриптам не подтверждено.
- User Secrets не читались. Существующее постороннее изменение README.md сохранено без правок.
- Docker недоступен: сборка Linux-образа, проверка CVE пакетов Debian/реального runtime, содержимое работающего контейнера и WebSocket в авторизованном браузере не проверены.
- Настройки Render/GitHub, веточные ограничения, доступ сотрудников, MFA, оповещения и фактические секреты окружения через панели управления не проверялись.
- Новые исправления не опубликованы: результаты публичной проверки относятся к уже работающему приложению, а не к локальным изменениям этого аудита. Commit/push не выполнялись.

## Проверка зависимостей

Выполнено через установленный .NET SDK 9.0.314:

```text
dotnet list EquipmentManager.sln package --vulnerable --include-transitive
```

Для локального выбора именно SDK 9 использовался запуск `dotnet "C:/Program Files/dotnet/sdk/9.0.314/dotnet.dll" ...`. Первый запрос в песочнице был заблокирован сетевыми ограничениями; повтор вне песочницы завершился с кодом 0. Источники: https://api.nuget.org/v3/index.json и локальный Microsoft SDK feed. Результат: оба проекта не содержат уязвимых пакетов по доступным данным этих источников; проверены также транзитивные зависимости. Это результат на дату аудита, не обещание отсутствия будущих CVE.

| Прямой пакет | Версия | Область |
| --- | --- | --- |
| Microsoft.AspNetCore.Authentication.JwtBearer | 9.0.13 | Приложение |
| Npgsql | 9.0.5 | Приложение |
| Microsoft.NET.Test.Sdk | 17.12.0 | Только тесты |
| xunit | 2.9.2 | Только тесты |
| xunit.runner.visualstudio | 2.8.2 | Только тесты |

Дополнительно просмотрен `dotnet list ... package --include-transitive --format json`: IdentityModel 8.0.1, Logging/DI abstractions 8.0.2; в тестах также Newtonsoft.Json 13.0.1 и зависимости TestPlatform/xUnit. Пакеты не устанавливались и не обновлялись. NuGet-проверка не сканирует ОС, shared framework ASP.NET Core, Docker-образы и встроенные JS/CSS. В wwwroot находится Bootstrap 5.3.3; приложение подключает его CSS, Bootstrap JS не подключён в App.razor. Подтверждённых advisories для этого использования в рамках проверки не установлено, отдельный JS/SBOM-аудит не проводился.

## Подтверждённые находки по приоритету

Оценка учитывает временный учебный стенд. «Подтверждено» означает наблюдаемое поведение либо конкретную конфигурацию; наличие настройки с риском не равнозначно доказанной эксплуатации.

| ID | Уровень | Находка и доказательство | Статус |
| --- | --- | --- | --- |
| — | Критический | Подтверждённых находок нет | В пределах области аудита |
| — | Высокий | Подтверждённых находок нет | Не утверждается безопасность всех облачных настроек |
| SEC-01 | Средний | `UiAccountEndpoints.ValidateFormAsync` проверял только Content-Length > 16384. Локальная валидная antiforgery-форма с дополнительным полем 20000 байт и chunked-передачей обходила ограничение: вместо 400 получала 302 после вызова Auth | Исправлено локально; эффект DoS не воспроизводился |
| SEC-02 | Средний | Явный RenderFreeProxy очищает KnownNetworks/KnownProxies и принимает последний X-Forwarded-Proto от любого непосредственного отправителя. Это подтверждено исходниками и существующими тестами RenderProxyTests | Оставлен согласованный учебный компромисс; внешняя эксплуатация не подтверждена |
| SEC-03 | Низкий | На публичном /login отсутствовали X-Content-Type-Options и Referrer-Policy. Это недостаток дополнительной защиты, а не доказанный XSS или утечка | Добавлены nosniff и same-origin локально |
| SEC-04 | Низкий | В API отсутствовала явная политика no-store, публичный 401 также её не содержал. Возможность раскрытия данных через кеш не установлена; Authorization уже ограничивает shared caching по HTTP | Добавлен no-store для /api и авторизованных HTML-страниц |
| SEC-05 | Низкий | Workflow использует изменяемые actions/checkout@v6 и actions/setup-dotnet@v4, Docker — теги образов без digest; нет lock-файлов NuGet и отдельного шага проверки CVE в CI | Остаточный риск цепочки поставок; компрометации не обнаружено. Предложены согласуемые изменения ниже |

### Выполненные исправления

1. Перед чтением antiforgery-формы устанавливается `IHttpMaxRequestBodySizeFeature.MaxRequestBodySize` не больше 16384 байт. Более строгий существующий лимит не увеличивается. Ограничение работает на потоке Kestrel независимо от Content-Length и применяется к входу/выходу, не к API или SignalR. Неверная/слишком большая форма даёт безопасный 400; ошибки разбора и 413 от сервера обрабатываются без исходного текста. Хосты без этой feature отдельно не проверены; целевой Kestrel её поддерживает.
2. `UseEquipmentSecurityHeaders` через OnStarting добавляет `X-Content-Type-Options: nosniff` и `Referrer-Policy: same-origin`, в том числе к отказам авторизации. CSP, frame-ancestors, WebSocket compression и origin-политики не заменяются.
3. Ответам /api и авторизованным HTML-страницам добавляется `Cache-Control: no-store`. Статические CSS/JS этим правилом не затрагиваются. На /login no-store уже обеспечивается механизмом формы/antiforgery.

## Анализ OWASP Top 10

| Категория 2021 → соответствие 2025 | Проверка и вывод |
| --- | --- |
| A01 Broken Access Control → A01 | Все API-маршруты требуют Bearer ReadPolicy; записи требуют отдельных Write/Fault-политик. Роли берутся из активного серверного profiles, а не user_metadata/JWT-ролей. Cookie не заменяет Bearer для API. CRUD повторно проверяет ограниченный session_user/current_user, роли и UUID в транзакции. Обход авторизации не обнаружен; реальная RLS не проверялась |
| A02 Cryptographic Failures → A04 | JWT: подпись, issuer, audience, lifetime, exp и алгоритм ES256/RS256; доверенный HTTPS JWKS и проверка use/alg/key_ops. Для БД обязателен VerifyFull, без административного CRUD-логина; CA публичный, отпечаток закреплён. Cookie защищена Data Protection. TLS endpoint/шифры БД в этом аудите не измерялись |
| A03 Injection → A05 | Данные SQL передаются типизированными $1/$2-параметрами; интерполяция SQL использует постоянные списки колонок/шаблоны, не пользовательские идентификаторы. Razor выводит данные как текст, MarkupString/Html.Raw/innerHTML для пользовательского ввода не найдено. Новый тест сохраняет script-строку только в локальной памяти и подтверждает HTML-экранирование на /equipment. SQL injection и XSS не подтверждены |
| A04 Insecure Design → A06 | Контекст пользователя задаётся сервером, операции имеют роли и версию для конфликта обновлений. SEC-01 исправлен. Локальный rate limiting, пагинация больших списков и общий бюджет circuit/сеансов не реализованы — рекомендации, эксплуатация не проверялась |
| A05 Security Misconfiguration → A02 | Production: HSTS, HTTPS redirection, безопасные ошибки; Render-режим требует явного opt-in и не включается одним RENDER=true. SEC-02 остаётся. SEC-03/04 исправлены. Настройки окружения Render не инспектировались |
| A06 Vulnerable and Outdated Components → A03 | NuGet direct/transitive: известных уязвимостей не найдено. .NET 9 ещё поддерживается на дату аудита, но поддержка заканчивается 10 ноября 2026 года. Фактическая patch-версия runtime Live неизвестна; планировать отдельную миграцию на .NET 10 LTS и сканирование контейнера |
| A07 Identification and Authentication Failures → A07 | Вход/выход требуют HTTPS и antiforgery. Cookie __Host-, Secure, HttpOnly, SameSite=Lax, без Domain, sliding expiration выключен. Access token только в памяти, refresh не сохраняется. Истечение/выход/перезапуск отзывают локальный сеанс. Профиль и права API проверяются заново. Требования к паролям, MFA и rate limiting Supabase не исследовались |
| A08 Software and Data Integrity Failures → A08/A03 | Нет произвольной десериализации типов/кода. JWT проверяется до чтения доверенных данных. CA проверяется SHA-256/CA/срок/подпись до установки. CI read-only, без секретов Supabase; облачный тест исключён фильтром и пустым EM_PG_TEST_RUN. SEC-05: pinning/SBOM/lock-файлы остаются рекомендацией |
| A09 Security Logging and Monitoring Failures → A09 | Прикладная диагностика API/Auth/Profiles записывает фиксированные категории, типы и SQLSTATE, без exception.Message, токенов и тел Auth. Тесты проверяют отсутствие секретных маркеров. Нет подтверждения внешних alerting/retention/аудита административных событий; доступ к Render Logs следует ограничить |
| A10 SSRF → A01; исключения → A10:2025 | Auth issuer и BackendBaseUrl задаются доверенной конфигурацией, HTTPS, без userinfo/query; адрес Backend не вычисляется из Host, redirects HttpClient выключены. JWKS не берётся из jku/x5u входного JWT. SSRF от пользовательского URL не найден. StoreException и неожиданные ошибки API преобразуются в безопасный ProblemDetails; cancelled request не маскируется под успешный ответ |

### CSRF, JWT и сеансы

Login/logout — обычные POST с ручной проверкой antiforgery, как без него, так и с чужим токеном Auth не вызывается. API использует Bearer из заголовка и не принимает cookie как полномочие; JSON-операции API не нуждаются в cookie-antiforgery. Не найдено разрешающего CORS. ReturnUrl разрешает только локальный путь и отсекает //, обратные слеши и управляющие символы; открытый redirect не обнаружен. Circuit перевалидирует локальный сеанс примерно каждые 15 секунд, а API актуальные роли проверяет при каждом запросе. Удалённый logout/смена пароля не гарантируют мгновенную криптографическую недействительность уже выданного JWT до exp; мгновенный централизованный отзыв не заявлен.

### Ошибки и секреты

StorageErrorFilter возвращает фиксированные сообщения и TraceId, не SQL, stack trace или ConstraintName. ProfileAuthorizationResultHandler корректно возвращает 503 при недоступности профилей, не разрешая операцию. SupabasePasswordClient не передаёт исходные исключения/тела в UI, named HttpClient-логирование для Auth/Backend отключено. JWT IncludeErrorDetails=false; результаты публичного фиктивного Bearer — пустой 401. Существующие ErrorHandlingTests, ProfileStoreDiagnosticsTests и AuthDiagnosticsTests входят в итоговый прогон.

Проверены 109 отслеживаемых текстовых файлов сигнатурами private key, GitHub token, sb_secret и JWT. Подозрительных совпадений не найдено; .env.example содержит placeholders. UserSecretsId, адрес проекта Supabase и публичный CA не являются секретами. Это не полный энтропийный скан и не аудит истории Git: отсутствие произвольного пароля во всех коммитах не доказано.

## Docker, конфигурация и CI/CD

- SDK-стадия восстанавливает и публикует только веб-проект. Runtime Debian .NET 9 работает от APP_UID, без root во время приложения, порт 10000. Secrets не передаются в Docker ARG/ENV исходников. Используется явный список COPY; .dockerignore исключает env, ключи, тесты, Git и все сертификаты, кроме выбранного публичного CA.
- Путь Root Certificate в шаблоне Linux: `/usr/local/share/ca-certificates/supabase-ca.crt`; VerifyFull не ослаблен. CA добавлен также в Debian trust store, срок до 26 апреля 2031. Системная установка расширяет доверие всему контейнеру; ротацию проводить только из официального источника с пересмотром отпечатка.
- В appsettings нет паролей, по умолчанию InMemory и localhost Backend; для публикации нужны переменные Render. В Production код не читает User Secrets. Реальные значения Environment не проверялись. .gitignore не удаляет ранее отслеженные секреты из истории.
- CI: push/PR master, ubuntu-latest, SDK 9.0.x, restore → Release build → обычные тесты → format; permissions contents:read, timeout 15 минут. pull_request_target и передача облачных секретов отсутствуют. Репозиторные настройки Auto-Deploy After CI Checks Pass известны из контекста пользователя, но независимо через API не подтверждались. Фактическая защита master и ограничения ручного деплоя не проверены.

## Публичная проверка Render

Обычные HTTP-запросы выполнены 17 октября 2026 года с отключёнными redirects и cookie-хранилищем клиента. Только восемь запросов, без SQL/XSS-полезных нагрузок на облаке, без больших тел и без входа.

| Запрос | Наблюдение |
| --- | --- |
| GET HTTP / | 301 на HTTPS того же домена |
| GET HTTPS /login | 200 text/html; HSTS max-age=2592000; CSP frame-ancestors 'self'; X-Frame-Options SAMEORIGIN; Cache-Control no-store, no-cache |
| GET HTTPS /equipment | 302 на /login; содержимое оборудования не раскрыто |
| GET HTTPS /api/equipment без Bearer | 401, пустое тело |
| GET HTTPS /api/equipment с фиктивным invalid-audit-token | 401, пустое тело, внутренних деталей нет |
| GET HTTPS /.env | 404 |
| GET HTTPS /appsettings.json | 404 |
| GET HTTPS /security_audit.md | 404 |

X-Content-Type-Options и Referrer-Policy в проверенных публичных ответах отсутствовали; локальное исправление ещё не опубликовано. Заголовок Server: cloudflare не раскрывает версию Kestrel/.NET. Наличие HTTPS/HSTS и 401 не заменяет проверку ролей: авторизованные и негативные ролевые сценарии выполнялись только в изолированных тестах.

## Потенциальные риски и согласуемые работы

Это условия возможных атак/эксплуатационные ограничения, а не подтверждённые инциденты:

1. Высокий условный риск SEC-02: прямой доступ недоверенного сервиса к Kestrel или недостаточная перезапись X-Forwarded-Proto ingress позволят подменять схему в RenderFreeProxy. Публичный обход не подтверждён. Для постоянного размещения согласовать инфраструктуру с известным доверенным прокси; автоматически угадывать IP нельзя. В ходе этого аудита ранее согласованный режим не менялся.
2. Средний потенциальный риск перебора/истощения ресурсов: локального login rate limiter нет; ограничения Supabase/Render не проверялись. Не вводить наивный per-IP лимит: за текущим proxy режимом RemoteIpAddress может быть общим ingress-адресом. Требуется отдельное проектирование защиты с достоверной идентификацией источника и проверка пользовательских сценариев.
3. Средний потенциальный риск больших авторизованных запросов: списки без пагинации и текстовые поля без прикладного максимума, бюджеты сеансов/пула требуют нагрузочной оценки. Глобальные лимиты Kestrel существуют; отсутствие любых ограничений не утверждается. Массовые/DoS-запросы не выполнялись. Ограничения длины и пагинация могут менять бизнес-функциональность, поэтому оставлены для согласования.
4. Низкий потенциальный риск клиентской защиты: текущая CSP ограничивает frame-ancestors, но не весь script-src/style-src/connect-src. Полную CSP с nonce/hash вводить отдельно с тестами ImportMap и Blazor/WSS; доказанного XSS нет.
5. Перезапуск/сон Render Free теряет circuits и серверные сеансы, требует повторного входа. Непостоянные Data Protection keys соответствуют текущему одно-процессному режиму; это ограничение доступности, само по себе не обход авторизации.
6. До 10 ноября 2026 года запланировать .NET 10 LTS. Обновление target framework/IdentityModel/контейнера, закрепление Docker digest и Actions SHA, NuGet lock-файлы, SBOM/контейнерный CVE scan, зависимостный gate CI и Dependabot требуют отдельного изменения и обслуживания; в этом аудите не выполнялись. Команда list --vulnerable сама по себе не является gate: нулевой exit code не гарантирует, что обнаруженные advisories остановят workflow.
7. Проверить отдельно MFA администраторов, права на Render Logs, ротацию секретов, веточные ограничения и аудит деплоев. Из текущего кода нельзя достоверно установить их наличие/отсутствие.

## Автоматические проверки и влияние исправлений

Новые SecurityAuditTests: известная и неизвестная длина формы >16 КиБ, корректный короткий chunked-вход, no-store/заголовки при 401 и успешном API/SSR, экранирование пользовательского HTML. Все данные этого сценария хранятся только в тестовом InMemory, Auth/JWKS/профили подменены. До исправления chunked-регрессия дала Expected 400 / Actual 302, после исправления проходит. Остальные прежние тесты проверяют JWT, роли, cookie, CSRF, ошибки, PostgreSQL-контракты и безопасную диагностику без основной БД.

Команды итоговой проверки:

```text
dotnet build EquipmentManager.sln --configuration Release --no-restore
dotnet test EquipmentManager.sln --configuration Release --no-build --no-restore --filter "Category!=PostgresIntegration"
dotnet format EquipmentManager.sln --verify-no-changes --no-restore
git diff --check
```

Итог: Release build — 0 ошибок и 0 предупреждений; 186 обычных тестов успешно, 0 ошибок и 0 пропусков; dotnet format --verify-no-changes и git diff --check пройдены. До аудита было 181 обычное тестовое выполнение, добавлено 5. Облачный PostgreSQL-тест не запускался. NuGet-проверка — 0 известных уязвимых пакетов обоих проектов по доступным источникам на дату аудита.

Изменяемые файлы: Program.cs; Services/HttpSecurityHeaders.cs; Services/Authentication/UiAccountEndpoints.cs; EquipmentManager.Tests/UiAuthFixture.cs; EquipmentManager.Tests/SecurityAuditTests.cs; security_audit.md. Пользовательский README.md не входит в изменения аудита.

При будущей публикации изменятся заголовки ответа; API/личный HTML перестанут кешироваться; слишком большие формы входа/выхода, в том числе без Content-Length, будут отклоняться. Корректные формы, существующая авторизация, JWT, cookie, роли, бизнес-правила, PostgreSQL, Supabase и SignalR не меняются. Production настройки и CI не изменены.

## Источники

- [OWASP Top 10:2021](https://owasp.org/Top10/2021/) и [OWASP Top 10:2025](https://top10.owasp.org/2025/).
- [Ограничение тела запроса ASP.NET Core](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.http.features.ihttpmaxrequestbodysizefeature?view=aspnetcore-9.0).
- [Forwarded headers и неизвестные прокси](https://learn.microsoft.com/en-us/dotnet/core/compatibility/aspnet-core/8.0/forwarded-headers-unknown-proxies).
- [CSP и Blazor](https://learn.microsoft.com/en-us/aspnet/core/blazor/security/content-security-policy?view=aspnetcore-9.0).
- [Политика поддержки .NET](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core).
- [Безопасность Npgsql](https://www.npgsql.org/doc/security.html) и [официальные advisories Bootstrap](https://github.com/twbs/bootstrap/security/advisories).
