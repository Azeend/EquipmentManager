# ДЗ6: Яндекс ID через Custom OAuth2 Supabase

## Архитектура и границы доверия

Вход email/password сохранён. Оба способа используют общие проверку JWT в
`SupabasePasswordClient` и создание сеанса в `UiAccountEndpoints`:
Supabase JWT → подпись/JWKS, issuer, audience, срок → UUID `sub` → активный профиль
PostgreSQL с разрешённой ролью → серверный UiSessionStore → Secure/HttpOnly cookie.
Яндекс ID и email не назначают роли. Приложение не создаёт профили и не использует service_role.

Адаптер GET `/oauth/yandex/userinfo` принимает только `Authorization: Bearer <Yandex access token>`.
Сервер отправляет токен только заголовком `Authorization: OAuth ...` на фиксированный
`https://login.yandex.ru/info?format=json`, проверяет `id` и точное соответствие `client_id`.
Успешный результат — только `{"sub":"<стабильный id>"}`. Email намеренно не возвращается.
Запросы и заголовки именованных HttpClient не журналируются. Ошибки содержат только
фиксированные категории, статусы и типы исключений, без тел ответов и токенов.

POST `/account/oauth/yandex` требует HTTPS и antiforgery. Приложение хранит PKCE verifier
на сервере, передаёт Supabase challenge S256, сохраняет независимую корреляцию в
Secure/HttpOnly/SameSite=Lax cookie. Собственный state включён в `redirect_to`, а state
Supabase между провайдером и Auth не переопределяется. GET `/account/oauth/callback`
атомарно потребляет состояние, проверяет исходный браузер и обменивает код серверным
POST `/auth/v1/token?grant_type=pkce`. После проверки JWT используются прежние проверки профиля.
Произвольные returnUrl и Host запроса не определяют внешний callback.

## Ручная настройка Supabase и Яндекса

Настройки пока не применены; перед включением требуется публикация подготовленного кода.

1. В существующем приложении Яндекс разрешить `login:info` (права email, телефона и API не нужны).
2. В Supabase Authentication → Providers → New Provider выбрать **Manual configuration / OAuth2**:

   | Поле | Значение |
   |---|---|
   | Identifier | `custom:yandex` |
   | Client ID | ID существующего приложения Яндекс |
   | Client Secret | Секрет существующего приложения; только в Supabase |
   | Authorization URL | `https://oauth.yandex.ru/authorize` |
   | Token URL | `https://oauth.yandex.ru/token` |
   | UserInfo URL | `https://equipment-manager-demo.onrender.com/oauth/yandex/userinfo` |
   | Scopes | `login:info` |
   | `pkce_enabled` | `true` |
   | `email_optional` | `true` — обязательно, адаптер не передаёт email |

   Issuer и JWKS не нужны для ручного OAuth2. Не выбирать OIDC/discovery и не подставлять
   вымышленные значения. Если интерфейс требует их именно для OAuth2 или не позволяет
   установить email_optional, остановить настройку и проверить доступную версию/официальный
   административный интерфейс Supabase. Не обходить ограничения фиктивным email или issuer.

3. Скопировать **Callback URL из формы Supabase**, установить его Redirect URI приложения
   Яндекс. Для обычного callback текущего проекта ожидается
   `https://cyzanurmyswswbicjiaf.supabase.co/auth/v1/callback`, но значение формы имеет приоритет.
   Это callback **Яндекс → Supabase**, а не адрес адаптера или приложения.
4. В Supabase Authentication → URL Configuration установить Site URL
   `https://equipment-manager-demo.onrender.com/` и добавить Redirect URLs:
   - `https://equipment-manager-demo.onrender.com/account/oauth/callback`
   - `https://localhost:7128/account/oauth/callback`

   Это callback **Supabase → приложение**. Динамический query `state` сохраняется при
   возврате; не добавлять wildcard для произвольных путей/доменов. Использовать HTTPS.
5. Разрешить **manual identity linking** в конфигурации Authentication Supabase.
   Не менять политики регистрации и не выдавать новые роли.

## Render и локальная среда

Добавить в Render Environment:

```text
YandexOAuth__Enabled=true
YandexOAuth__ClientId=<ID существующего приложения Яндекс>
```

Проверить существующее значение:

```text
SupabaseUiAuth__BackendBaseUrl=https://equipment-manager-demo.onrender.com/
```

Настройки SupabaseAuth Issuer/Audience/SigningAlgorithm, publishable key, PostgreSQL и TLS
остаются прежними. Yandex Client Secret, service_role и OAuth refresh token в Render не нужны.
По умолчанию YandexOAuth.Enabled=false; текущая опубликованная авторизация не зависит от провайдера.

Локально включать те же параметры через окружение, BackendBaseUrl должен быть
`https://localhost:7128/`. Доверить dev HTTPS-сертификат стандартным способом.
Userinfo URL в Supabase остаётся публичным Render-адресом даже при локальном входе:
облачный Supabase не может вызвать localhost. Поэтому сначала опубликовать адаптер на Render.

## Привязка существующего профиля

Сначала войти существующим email/password, открыть `/account/session`, нажать
«Привязать Яндекс». Защищённый POST вызывает официальный Supabase
`GET /auth/v1/user/identities/authorize` с пользовательским access token, PKCE и
`skip_http_redirect=true`. Проверяется HTTPS URL ответа с фиксированным хостом
`oauth.yandex.ru` и путём `/authorize`. Callback требует тот же действующий локальный
сеанс и исходный Supabase UUID. Затем профиль повторно проверяется и cookie обновляется.
После привязки доступна кнопка «Войти через Яндекс» на `/login`; пароль продолжает работать.

Первый самостоятельный OAuth-вход без привязки может создать в Supabase Auth нового
пользователя (зависит от политики signup). У него нет PostgreSQL-профиля и доступа к приложению.
Никакого сопоставления с существующим профилем по email или ручной подмены UUID не выполнять.
Если Яндекс уже связан с другим Supabase-пользователем, привязка должна быть отклонена Supabase;
автоматическое объединение/перенос профилей приложением не предусмотрены.

## Ограничения и проверка после настройки

- Без `email_optional=true` и manual linking существующего UUID рабочий вход не гарантируется.
  При несовместимости доступной конфигурации OAuth2 не ослаблять проверки.
- State живёт пять минут, однократно потребляется; хранилище ограничено 1024 попытками.
  Один браузер поддерживает одну текущую попытку; новая попытка заменяет correlation cookie.
  Сон/перезапуск Render или смена процесса могут прервать вход: начать заново.
  Для нескольких реплик потребовалось бы общее серверное хранилище, сейчас оно не добавляется.
- Публичный адаптер ограничивает размер ответа и время ожидания, но требует исходящего
  доступа Render к Яндексу и входящего доступа Supabase к Render. Free cold start может
  превысить таймаут провайдера. Нагрузочная защита платформы остаётся отдельным ограничением.
- Не включать журналирование Authorization, тел Auth и полных URL callback с code/state,
  включая внешние прокси/Render. Callback имеет no-store и Referrer-Policy: no-referrer.
- Пользовательский выход отзывает локальный/Supabase-сеанс, но не глобальную сессию Яндекса.
- После настройки вручную проверить: привязку → выход → вход Яндекс → чтение API;
  отказ пользователя → повторный вход; отсутствие профиля → отказ без прав.
  Локальные тесты изолируют транспорт, JWKS и профили; реальный облачный OAuth ими не подтверждается.

Официальные источники:
[Supabase Custom OAuth2](https://supabase.com/docs/guides/auth/custom-oauth-providers),
[PKCE](https://supabase.com/docs/guides/auth/sessions/pkce-flow),
[Identity linking](https://supabase.com/docs/guides/auth/auth-identity-linking),
[Яндекс Userinfo](https://yandex.ru/dev/id/doc/ru/user-information).
