# EquipmentManager

Учебное приложение для учёта промышленного оборудования, регистрации неисправностей и обслуживания. Интерфейс Blazor подключён к защищённому Backend API, данные сохраняются в Supabase PostgreSQL. Лабораторная №6 добавляет CI/CD, аудит безопасности, OAuth2, аналитику и мониторинг.

[Открыть приложение](https://equipment-manager-demo.onrender.com/) · [Репозиторий GitHub](https://github.com/Azeend/EquipmentManager) · [Общая документация интеграций](integration_documentation.md)

## Технологии

- C#, ASP.NET Core .NET 9, Blazor InteractiveServer, Razor, Bootstrap.
- Supabase PostgreSQL, Npgsql; Supabase Auth и JWT Bearer для API.
- Cookie-сеанс интерфейса с хранением access token только на сервере.
- xUnit и Microsoft.NET.Test.Sdk; сборка через .NET SDK/NuGet, без npm.

## Основные функции

- `/equipment`: поиск, создание, редактирование и удаление оборудования; обязательные поля, допустимые состояния, уникальный инвентарный номер без учёта регистра и крайних пробелов.
- `/faults`: регистрация → «Новая» → «В работе» → «Завершена»; проверка версии и обязательные работы/результат.
- `/maintenance-history`: одна запись на завершённую заявку, фильтр по оборудованию и сортировка по дате.
- `/login`, `/account/session`: вход, состояние сеанса и выход.
- Инженер управляет оборудованием и регистрирует заявки; специалист регистрирует и обрабатывает заявки; руководитель читает данные. Связанное оборудование удалить нельзя.

Вход доступен по email/password и через привязанный Яндекс ID. По подтверждению владельца, опубликованные интеграции работают: Метрика получает просмотры, оба Health Checks возвращают Healthy, UptimeRobot показывает Up, JSON-логи доступны в Render. Локальный набор включает **280 обычных .NET-тестов и 10 JS-тестов**; облачный PostgreSQL-тест запускается отдельно, только явно разрешённым способом.

## Запуск в Visual Studio

1. Установить Visual Studio с поддержкой .NET 9 и компонентом ASP.NET либо совместимый SDK и runtime .NET 9. Открыть `EquipmentManager.sln`, выбрать `EquipmentManager` запускаемым проектом.
2. Для нового проекта Supabase подготовить схему, ограниченные подключения и профили по [инструкции Backend](backend_documentation.md). Уже установленную базу повторно не создавать.
3. ПКМ по веб-проекту → **Управление секретами пользователя**. Локально задать `ConnectionStrings:Profiles` (ограниченный читатель профилей `equipment_app`), `SupabaseUiAuth:PublishableKey`, а для постоянного хранения — `ConnectionStrings:EquipmentCrud` (ограниченный `equipment_api`). Подробности и имена переменных окружения приведены в инструкции Backend. Пароли/строки подключения в репозиторий не помещать.
4. Проверить, что `SupabaseAuth:Issuer`, `Audience`, `SigningAlgorithm` и publishable key относятся к одному проекту. Текущие общие настройки — Audience `authenticated`, алгоритм `ES256`; для своего проекта подтвердить их фактическое соответствие. Включение Auth требует `Enabled=true` и `SettingsConfirmed=true`.
5. Выбрать **https**, доверить сертификат разработки, нажать F5. Адрес: `https://localhost:7128/`; `SupabaseUiAuth:BackendBaseUrl` должен совпадать с этим HTTPS origin и заканчиваться `/`. Войти существующей учётной записью с активным профилем.

Защищённая cookie требует HTTPS. После остановки процесса нужен повторный вход. Строки подключений и ключ Auth читаются только из User Secrets в Development или переменных окружения. User Secrets не входят в каталог репозитория. `.env` и `appsettings.Local.json` автоматически не загружаются.

## Выбор хранения

| `Storage:Provider` | Поведение |
| --- | --- |
| `InMemory` | Все три сущности в общей памяти процесса; пять демонстрационных записей оборудования, данные теряются при перезапуске |
| `PostgreSQL` | Все три PostgreSQL-хранилища через `equipment_api`, сохранение между перезапусками |

В общем `appsettings.json` намеренно оставлен `InMemory`. Рабочий запуск с БД использует локальное переопределение `Storage:Provider=PostgreSQL` в User Secrets либо `Storage__Provider=PostgreSQL`. Переключатель применяется после перезапуска. Источники не смешиваются, при ошибке БД подмены памятью нет, демонстрационные данные автоматически в БД не переносятся. Даже InMemory требует работающих Auth и чтения профилей для входа и проверки ролей.

## Сборка и тестирование

Остановить отладку перед обычной пересборкой:

```powershell
dotnet restore EquipmentManager.sln
dotnet build EquipmentManager.sln --no-restore
dotnet test EquipmentManager.sln --no-build --no-restore --filter "Category!=PostgresIntegration"
node --test EquipmentManager.Tests/metrika.test.mjs
dotnet format EquipmentManager.sln --verify-no-changes --no-restore
dotnet run --project EquipmentManager.csproj --launch-profile https
```

Облачный тест сохранён и исключён из обычного запуска. Результаты и границы проверки — [backend_testing.md](backend_testing.md).

## Непрерывная интеграция — ДЗ6

Workflow [CI](.github/workflows/ci.yml) запускается при `push` в `master` и для `pull_request`, направленных в `master`. На `ubuntu-latest` устанавливается .NET SDK `9.0.x`, затем последовательно выполняются восстановление зависимостей, сборка Release, обычные xUnit-тесты и проверка форматирования. Ошибка любого этапа останавливает следующие этапы.

```powershell
dotnet restore EquipmentManager.sln
dotnet build EquipmentManager.sln --configuration Release --no-restore
dotnet test EquipmentManager.sln --configuration Release --no-build --no-restore --filter "Category!=PostgresIntegration"
dotnet format EquipmentManager.sln --verify-no-changes --no-restore
```

Секреты Supabase для CI не требуются: обычные тесты используют локальные фикстуры и подмены внешних зависимостей. Облачный тест исключён фильтром `Category!=PostgresIntegration`; дополнительно `EM_PG_TEST_RUN` в workflow оставлен пустым, поэтому разрешение подключения не выдаётся. Проверка форматирования не изменяет файлы; для локального исправления пробелов используйте `dotnet format whitespace EquipmentManager.sln --no-restore` и проверьте diff.

Результаты доступны в **Actions → CI**. Workflow выполняет только CI; автоматический деплой Docker из `master` настроен на стороне Render в режиме **After CI Checks Pass**. JS-тесты запускаются отдельно через Node.js (без npm-пакетов), текущий workflow их не включает.

## Подготовка к Render — ДЗ6

Приложение опубликовано как Render Free Web Service (Docker, `master`, Frankfurt). Многоэтапный Dockerfile запускает приложение от непривилегированного пользователя; публичный CA Supabase проверяется и устанавливается при сборке. Порядок настройки HTTPS/SignalR, Session Pooler и автоматического деплоя **After CI Checks Pass** описан в [инструкции Render](render_deployment.md). [.env.example](.env.example) содержит только шаблон; приложение не загружает этот файл автоматически.

Обработка forwarded headers по умолчанию выключена. Обычный режим требует подтверждённых IP прокси; отдельный явно выбранный `Hosting__RenderFreeProxy=true` предназначен для временного Render Free и доверяет ingress платформы без проверки IP. Ограничения этого учебного компромисса и настройки приведены в инструкции.

## Документация

| Материал | Назначение |
| --- | --- |
| [integration_documentation.md](integration_documentation.md) | Итоги ДЗ6: архитектура, интеграции, настройки и проверка |
| [security_audit.md](security_audit.md) | Аудит безопасности, исправления и остаточные риски на дату аудита |
| [oauth_yandex.md](oauth_yandex.md) | Яндекс OAuth2 через Supabase, PKCE, привязка существующего пользователя |
| [metrika_integration.md](metrika_integration.md) | Счётчик 113657931, безопасные просмотры Blazor и ограничения |
| [health_monitoring.md](health_monitoring.md) | Liveness/readiness, PostgreSQL SELECT 1, HTTP 200/503 |
| [structured_logging.md](structured_logging.md) | JSON Console, события, фильтрация Production и Render Logs |
| [render_deployment.md](render_deployment.md) | Docker, переменные Render, сертификат и ограничения прокси |
| [backend_documentation.md](backend_documentation.md) | Актуальная архитектура, настройка, порядок SQL, API и примеры HTTP |
| [backend_testing.md](backend_testing.md) | Шаг 8: 140 тестов, результаты сборки, незапущенный облачный тест |
| [error_handling_logging.md](error_handling_logging.md) | Шаг 7: ошибки и диагностика в ASP.NET/Postgres Logs |
| [database/schema_description.md](database/schema_description.md) | Четыре таблицы, связи и ограничения |
| [database/006_3_integration_test.md](database/006_3_integration_test.md) | Отдельный opt-in тест только для EquipmentManager-Test |
| [technical_specification.md](technical_specification.md), [user_stories.md](user_stories.md) | Требования и пользовательские сценарии |
| [development_report.md](development_report.md), `frontend_*.md`, `ui_concepts/` | История предыдущих лабораторных и концепции интерфейса |

Код находится в `Components/`, `Models/`, `Services/`, `Validators/`; тесты — в `EquipmentManager.Tests/`. Ранние документы настройки в `database/` сохраняют результаты своего этапа; актуальные состояние и порядок установки приведены в Backend-документации. Сводные показатели, API регистрации Auth-пользователей и произвольное изменение истории не реализованы.

## Публикация в GitHub

`.gitignore` исключает bin/obj, TestResults, локальные настройки IDE, User Secrets-файлы, `.env`, локальные appsettings, ключи/сертификаты и временные диагностические файлы. `appsettings.json`, `appsettings.Development.json` и `Properties/launchSettings.json` содержат только общие настройки. `UserSecretsId` и публичный URL Supabase не являются секретами. Исключения `.env.example`/`.env.sample` допустимы только с placeholders.

Перед публикацией проверить выбранные для добавления файлы и содержимое индекса: `.gitignore` не удаляет уже отслеживаемые файлы и не очищает историю. Приложение, результаты тестов и документация не должны содержать реальные пароли, access/refresh JWT или строки подключения с секретами. Выполнение commit/push остаётся отдельным действием пользователя.
