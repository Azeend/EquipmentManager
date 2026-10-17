# Размещение EquipmentManager на Render

Подготовлена публикация одного ASP.NET Core процесса через Docker. Supabase Auth, роли, REST API и SQL остаются прежними. Сервис и настройки GitHub пока не созданы и не изменены.

## Образ и порт

Dockerfile восстанавливает и публикует только веб-проект в SDK 9.0, затем копирует результат в runtime ASP.NET 9.0. Финальный процесс работает от встроенного непривилегированного пользователя `$APP_UID`. Исходники тестов, локальные настройки, секреты и Git исключены из контекста; COPY дополнительно ограничен каталогами приложения. Секреты передаются только при запуске, не через build args.

Kestrel слушает `http://0.0.0.0:10000`, Render завершает TLS на входном прокси. В Environment оставить `PORT=10000` и соответствующий `ASPNETCORE_URLS`; при смене порта менять оба значения. EXPOSE сам по себе порт не настраивает. Docker Command оставьте пустым.

Локально при наличии Docker: `docker build -t equipmentmanager:lab6 .`. Для запуска нужны отдельные параметры окружения; `.env.example` содержит только шаблоны и автоматически приложением не читается. Не использовать реальные секреты в команде сборки.

## HTTPS и SignalR

Render поддерживает WebSockets без отдельного сервиса SignalR. Blazor InteractiveServer и API работают в одном Web Service и на одном порту. Использовать публичный HTTPS origin собственного сервиса как `SupabaseUiAuth__BackendBaseUrl`, с завершающим `/`: серверный клиент обращается к API этого же приложения.

В коде по умолчанию оба режима выключены: `Hosting__RenderProxy=false`, `Hosting__RenderFreeProxy=false`. Само `RENDER=true` ничего не включает. Режимы взаимоисключающие; одновременное включение останавливает запуск.

**Обычный строгий режим:** `Hosting__RenderProxy=true`, `Hosting__RenderFreeProxy=false` и непустой список подтверждённых IP непосредственных прокси: `Hosting__KnownProxies__0`, `__1` и далее. Без списка либо с неверными адресами запуск останавливается. Принимаются конкретные IPv4/IPv6, без wildcard/CIDR/DNS. Недоверенный источник не может подменить схему; встроенное доверие loopback заменяется точным списком.

**Выбранный учебный режим Render Free:** `Hosting__RenderFreeProxy=true`, `Hosting__RenderProxy=false`. Дополнительно проверяется платформенное `RENDER=true`, но оно только защищает от случайного запуска режима вне платформы, а не доказывает происхождение заголовка. Список KnownProxies не нужен: в этой явно выбранной ветке ForwardedHeadersMiddleware очищает списки доверия и принимает схему от любого непосредственного отправителя. Это штатная возможность ASP.NET Core с известным ослаблением проверки источника; она не включена в Dockerfile или общей конфигурации. Шаблон .env.example намеренно выбирает её для Render Free.

Оба режима принимают только последний `X-Forwarded-Proto` (`ForwardLimit=1`), до HSTS, HTTPS redirect, cookie и antiforgery. `X-Forwarded-Host` и `X-Forwarded-For` игнорируются. Не включать глобально `ASPNETCORE_FORWARDEDHEADERS_ENABLED` или переключатель совместимости `IgnoreUnknownProxiesWithoutFor`. В `AllowedHosts` указать точный домен без схемы; дополнительные собственные домены перечислить через `;`.

### Граница доверия временного учебного развёртывания

IP ingress не угадываются и не берутся из Outbound IP Addresses. Вместо проверки IP в учебном режиме принято доверие HTTPS ingress и сетевой изоляции Render: порт контейнера не опубликован напрямую в Интернете. Требуется, чтобы ingress формировал достоверный последний X-Forwarded-Proto, а не пропускал подставленную клиентом схему. После деплоя проверить запросы HTTPS и HTTP, в том числе с подставленным X-Forwarded-Proto; HTTPS не должен превращаться в HTTP, HTTP должен перенаправляться на HTTPS. Если это условие не выполняется, остановить использование стенда и перейти к строгому режиму на контролируемом прокси.

**Остаточный риск:** любой процесс/сервис, получивший прямой доступ к Kestrel, в том числе через приватную сеть при наличии такой связи, сможет подменить схему. Переменная RENDER и AllowedHosts этот риск не устраняют. Не размещать недоверенные соседние сервисы, не публиковать порт контейнера напрямую и не использовать RenderFreeProxy на обычном публичном сервере. Это согласованный компромисс для временного учебного стенда, не эквивалент строгому доверию IP. Для постоянного размещения — контролируемый Nginx со стабильным IP, закрытый Kestrel, TLS на Nginx и перезапись X-Forwarded-Proto значением `$scheme`, с передачей WebSocket Upgrade/Connection. Локальный Nginx в Render, просто доверяющий произвольным входящим заголовкам, происхождение схемы не подтверждает.

Cookie сохраняет Secure, HttpOnly, SameSite=Lax и префикс __Host-. Пароли и JWT не сохраняются в cookie. HTTPS для внешних запросов обязателен; сертификат разработки в контейнере не нужен. Восстановленная HTTPS-схема предотвращает повторные redirect и позволяет штатно работать antiforgery и интерактивному подключению. JWT, роли и REST API этим режимом не изменяются.

Оставить один экземпляр. UiSessionStore и Blazor circuits находятся в памяти; перезапуск, сон бесплатного сервиса или деплой прерывают интерактивные подключения и требуют повторного входа. Существующая логика намеренно инвалидирует сеансы после перезапуска. Data Protection может использовать непостоянные ключи контейнера: их сохранение само по себе не сохраняет UiSessionStore. Масштабирование и непрерывность сеансов этим этапом не реализуются. Диск для бизнес-данных не нужен — они находятся в PostgreSQL.

## Окружение и Supabase

Скопировать имена параметров из [.env.example](.env.example) в Render Environment и заменить все шаблоны. Подтвердить issuer, audience и алгоритм действующего проекта; ES256 в примере соответствует текущей конфигурации. `SettingsConfirmed=true` ставить после проверки. Publishable key — ключ того же проекта; `service_role`/`sb_secret` не использовать. Строки подключений и ключ вводить в интерфейсе Render, не коммитить и не публиковать в отчёте. User Secrets в Production не читаются.

Для существующего Supabase использовать Session Pooler, host из Dashboard → Connect, порт 5432. Логины: `equipment_app.<project-ref>` для чтения профилей и `equipment_api.<project-ref>` для CRUD; пароль каждого существующего ограниченного логина, не пароль postgres. Формат — Npgsql `Host=...;Port=...`, не URI. Сохранять SSL Mode=VerifyFull; не заменять имя хоста IP-адресом и не отключать проверку сертификата. Если провайдер требует отдельный корневой сертификат, настройте его доверие по инструкции Supabase, не снижая TLS-проверку. Пароли со специальными символами оформить по правилам Npgsql connection strings.

Суффикс проекта поддерживается текущими валидаторами; сервер проверяет фактическую роль equipment_api и RLS внутри каждой CRUD-транзакции. Session Pooler обеспечивает IPv4. Начальный Maximum Pool Size=5 для каждого из двух пулов ограничивает число соединений; согласовать с лимитом существующего проекта. Transaction Pooler на 6543 для этого стенда не выбирать. Миграции, создание пользователей и изменение Supabase в деплой не включены. Облачный PostgresIntegration в CI по-прежнему исключён; EM_PG_TEST_* в Render не нужны.

## Действия в Render

1. Для согласованного временного стенда выбрать RenderFreeProxy и принять описанную границу доверия. После отдельного согласованного commit/push убедиться, что CI для нужного коммита в master успешен.
2. Render Dashboard → New → Web Service → подключить GitHub как Git Provider. Разрешить доступ к Azeend/EquipmentManager и выбрать этот репозиторий. Не выбирать Public Git Repository или Existing Image: автоматическое ожидание CI требует подключения Git Provider.
3. Указать имя сервиса (это определяет `<service-name>.onrender.com`), Branch `master`, Language/Runtime `Docker`, Root Directory оставить пустым, Dockerfile Path `./Dockerfile`, Docker Build Context `.`. Выбрать регион по возможности близко к Supabase, тариф Free и один экземпляр.
4. В Advanced/Environment добавить параметры из шаблона, включая публичный домен выбранного имени, `Hosting__RenderFreeProxy=true` и `Hosting__RenderProxy=false`. KnownProxies не задавать, RENDER предоставляет платформа автоматически. Docker Command и Pre-Deploy Command оставить пустыми. Health Check Path задать `/login`: это публичная SSR-страница, проверяет HTTP-ответ без обращения к основной БД; готовность Auth/БД проверяется отдельно после входа. Проверка 3xx сама по себе не подтверждает правильную HTTPS-схему.
5. Если поле Auto-Deploy доступно при создании, выбрать Off для подготовки, затем Create Web Service. Первый деплой создания запускается отдельно от последующего автоматического ожидания CI: создавать сервис только на уже успешно проверенном коммите. Если поле доступно лишь после создания, сразу открыть Settings → Build & Deploy → Auto-Deploy.
6. В Settings → Build & Deploy → Auto-Deploy выбрать **After CI Checks Pass** и сохранить. Не выбирать On Commit. Render ждёт проверки нового коммита master; при провале или отсутствии checks деплой не запускается. В терминах Render success, neutral и skipped считаются успешными; наш CI не содержит условного пропуска основного job. Это встроенное CD Render, дополнительный GitHub deploy workflow/token/hook не нужен. Ручной Deploy не обеспечивает такой барьер — использовать только для заранее проверенного коммита.
7. Проверить Logs: приложение слушает порт 10000, нет ошибок прав пользователя, TLS или конфигурации. Открыть публичный HTTPS `/login`: нет циклических редиректов, страница и static assets загружаются. HTTP должен переводиться на HTTPS.
8. Войти существующей учётной записью Supabase; проверить Secure/HttpOnly cookie без JWT и запрос WebSocket `wss://<домен>/_blazor` со статусом 101 в DevTools (Network → WS). Проверить интерактивную навигацию, роли, API и сохранение данных штатным учебным сценарием. Не копировать токены/пароли в отчёт. После перезапуска ожидается повторный вход, сохранённые в БД данные остаются.
9. При следующем согласованном push проверить порядок: успешный GitHub CI → событие автоматического деплоя Render на том же commit SHA. Не инициировать push с намеренно сломанным кодом для проверки блокировки.

## Границы локальной проверки

Проверки .NET и middleware выполняются без соединения с Supabase. Полный Docker build, работа Linux-контейнера, фактическая цепочка прокси Render, WebSocket в облаке и TLS к Session Pooler должны быть подтверждены после создания сервиса: локальные unit-тесты не заменяют эту проверку.

Обоснование границы доверия: [изменение ASP.NET Core для неизвестных прокси](https://learn.microsoft.com/en-us/dotnet/core/compatibility/aspnet-core/8.0/forwarded-headers-unknown-proxies). Альтернативное размещение: [ASP.NET Core за Nginx](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/linux-nginx?view=aspnetcore-9.0). [Outbound IP Addresses Render](https://render.com/docs/outbound-ip-addresses) не является списком доверенных ingress-прокси.

Документация: [Docker](https://render.com/docs/docker), [порты Web Service](https://render.com/docs/web-services), [WebSockets](https://render.com/docs/websocket), [After CI Checks Pass](https://render.com/docs/deploys), [forwarded headers ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer?view=aspnetcore-9.0), [подключения Supabase](https://supabase.com/docs/guides/database/connecting-to-postgres).
