# ДЗ6: минимальный мониторинг состояния

Используется встроенный ASP.NET Core Health Checks (`AddHealthChecks`, `IHealthCheck`,
`MapHealthChecks`). Дополнительные пакеты, сервисы мониторинга и фоновые опросы не добавляются.

## Endpoints после деплоя

Оба адреса доступны без входа по HTTPS:

- `https://equipment-manager-demo.onrender.com/health/live`
- `https://equipment-manager-demo.onrender.com/health/ready`

```powershell
curl.exe -i https://equipment-manager-demo.onrender.com/health/live
curl.exe -i https://equipment-manager-demo.onrender.com/health/ready
```

Можно открыть эти же ссылки в браузере. Для локальной проверки использовать HTTPS-профиль
и адреса `https://localhost:7128/health/live` и `https://localhost:7128/health/ready`.

| Endpoint | Что проверяется | HTTP 200 | HTTP 503 |
|---|---|---|---|
| `/health/live` | Приложение запущено и не завершает работу | `{"status":"Healthy"}` | `{"status":"Unhealthy"}` |
| `/health/ready` | Liveness и необходимые PostgreSQL-подключения | `{"status":"Healthy"}` | `{"status":"Unhealthy"}` |

Разрешены GET и HEAD. POST возвращает 405 и не запускает проверку. Ответы не кешируются
браузером/прокси (`Cache-Control: no-store, no-cache`). Degraded также преобразуется в HTTP 503.
Публичный JSON содержит только общий статус: без имён зависимостей, адресов, ошибок, строк
подключения, паролей, JWT, данных таблиц и исключений.

## Проверка PostgreSQL

Используются те же защищённые источники конфигурации, что и в приложении:

- при `SupabaseAuth__Enabled=true` требуется `ConnectionStrings__Profiles`;
- при `Storage__Provider=PostgreSQL` требуется `ConnectionStrings__EquipmentCrud`.

На Render используются оба подключения. Каждое выполняет только **`SELECT 1`**.
Рабочие пулы, пользовательские сеансы, роли PostgreSQL, RLS и таблицы не меняются.
Для пробы открываются отдельные короткоживущие подключения без пула и ambient transaction.
Параметры Supabase Session Pooler, `SSL Mode=VerifyFull` и Root Certificate берутся из прежних
строк; проверка TLS не ослабляется. Отсутствие обязательной строки или небезопасный SSL Mode
означает Unhealthy до попытки подключения.

Проверки обоих подключений выполняются параллельно с **общим бюджетом 4 секунды**:
connect timeout — 3 секунды, command timeout — 2 секунды, ожидание отмены команды — 1 секунда.
Внешний предел ожидания пробы обеспечивает возврат результата даже для реализации,
не соблюдающей CancellationToken. При таймауте возвращается 503; liveness остаётся независимым.

Результат PostgreSQL-пробы, включая отказ, кешируется в памяти на **5 секунд после завершения**.
Параллельные запросы объединяются: только одна проба обращается к БД. Поэтому обнаружение
отказа/восстановления может задержаться до следующей пробы. Это защищает ограниченные
подключения Session Pooler от частых публичных запросов. Снаружи HTTP-кеширование выключено.

Для явного демонстрационного режима InMemory с отключённым Supabase Auth внешних
PostgreSQL-зависимостей нет: readiness может быть Healthy без БД. При текущих production
настройках отсутствие хотя бы одного обязательного подключения даёт 503.

## Журналирование

Категория: `EquipmentManager.Services.Monitoring.HealthStateTransitions`.
Событие: `HealthStateChanged`, EventId **6001**.

Записываются первое наблюдение и изменения состояния каждой проверки:

```text
Проверка: application; раньше: Unknown; сейчас: Healthy; категория: none; тип: none
Проверка: postgresql; раньше: Healthy; сейчас: Unhealthy; категория: connection; тип: NpgsqlException
Проверка: postgresql; раньше: Unhealthy; сейчас: Healthy; категория: none; тип: none
```

Healthy — Information, Unhealthy — Warning. Категории отказа: connection, timeout,
configuration, unexpected. Повторяющееся состояние не создаёт новых сообщений перехода.
Сообщения и экземпляры исходных исключений в журнал не передаются. Стандартный журнал
`Microsoft.Extensions.Diagnostics.HealthChecks` отключён для исключения повторов и
небезопасного вывода произвольных исключений; собственный безопасный журнал остаётся включён.
Проверки выполняются по запросу, а не по таймеру, поэтому без обращений новых событий не будет.

## Ограничения и ручная проверка

Новых переменных Render не требуется. Настройки Render/Supabase/БД при реализации не менялись.
После разрешённого деплоя выполнить два GET-запроса и проверить статус/JSON и переходы в Logs.
Для текущего работающего приложения ожидаются 200 и 200. Если live=200, ready=503,
процесс работает, но проверка зависимости не пройдена: искать postgresql и категорию отказа
в журнале. Не подставлять временно неверные production-секреты ради проверки 503.
Отказы и восстановление воспроизводятся локальными автоматическими тестами с подменой пробы.

`SELECT 1` подтверждает подключение и выполнение простого запроса, но не проверяет
доступ к каждой прикладной таблице, назначения ролей, OAuth-провайдера, Supabase Auth/JWKS
или все операции API. Это не полный аудит функциональности.

Render Free может находиться в спящем состоянии: холодный старт происходит до выполнения
endpoint и не входит в четырёхсекундный бюджет проверки БД. Для ручного curl при необходимости
увеличить клиентское время ожидания и повторить запрос. Эти endpoints не создают отдельный
внешний монитор, уведомления или механизм поддержания сервиса постоянно активным.
Если позднее выбирать platform health check, liveness не зависит от кратковременного сбоя БД;
настройка платформы на этом шаге не выполняется.

## Автоматические проверки

```text
dotnet build EquipmentManager.sln --configuration Release --no-restore
dotnet test EquipmentManager.sln --configuration Release --no-build --no-restore --filter "Category!=PostgresIntegration"
dotnet format EquipmentManager.sln --verify-no-changes --no-restore
```

Тесты используют локальный HTTP-конвейер с подменой сетевой пробы и без реальных секретов:
200/503, независимый live, защита ответа и журналов, восстановление, кеш, параллельные запросы,
таймаут, методы HTTP и отсутствие обязательной конфигурации. Реальные облачные подключения
и изменение данных в обычных тестах не выполняются.

[Официальная документация ASP.NET Core Health Checks](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/health-checks?view=aspnetcore-9.0).
