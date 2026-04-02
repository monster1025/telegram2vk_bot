# Telegram → VK: репост из канала в сообщество

Сервис на **ASP.NET Core** слушает посты в **Telegram-канале** (long polling), публикует их на **стене сообщества ВКонтакте** и при редактировании поста в Telegram обновляет соответствующий пост ВК. Соответствие «сообщение Telegram ↔ пост ВК» хранится в **SQLite**.

Поддерживаются:

- текст и подписи к фото;
- несколько фото в одном посте ВК (альбомы из Telegram собираются в один пост);
- правки поста в Telegram → `wall.edit` в ВК.

## Требования

- [.NET 10 SDK](https://dotnet.microsoft.com/download) — для локального запуска без Docker;
- Docker и Compose с поддержкой **Compose file 3.8+** (для `healthcheck.start_period`) — для контейнера.

## Быстрый старт (Docker)

1. Склонируйте репозиторий и перейдите в каталог проекта.

2. Создайте файл `.env` рядом с `docker-compose.yml`. Переменные конфигурации должны соответствовать [соглашению ASP.NET Core](https://learn.microsoft.com/aspnet/core/fundamentals/configuration/#environment-variables): секция и свойство разделяются **двойным подчёркиванием** `__`.

   Пример:

   ```env
   TELEGRAM__BotToken=123456:ABC...
   TELEGRAM__ChannelId=-1001234567890
   VK__AccessToken=vk1.a...
   VK__GroupId=123456789
   VK__ApiVersion=5.199
   ```

   - **TELEGRAM__ChannelId** — ID канала (для каналов часто вида `-100…`).
   - **VK__GroupId** — числовой ID сообщества **без** минуса (в API владелец стены задаётся как отрицательный ID группы).

3. Добавьте бота в канал как администратора (чтобы приходили обновления о постах).

4. Токен ВК должен позволять публикацию от имени сообщества (`wall`, загрузка фото и т.д. — по вашей настройке приложения).

5. Запуск:

   ```bash
   docker compose build
   docker compose up -d
   ```

   База по умолчанию: том `./data` → `/data/mapping.sqlite` в контейнере (см. `DB__Path` в `docker-compose.yml`).

## Локальный запуск (без Docker)

```bash
cd src/Telegram2VkBot
dotnet run
```

Перед запуском задайте те же переменные окружения или отредактируйте `appsettings.json` (секции `TELEGRAM`, `VK`, `DB`). Путь к SQLite по умолчанию: `data/mapping.sqlite` относительно рабочей директории.

HTTP-сервер слушает адрес из `ASPNETCORE_URLS` (в Docker: `http://+:8080`).

## Проверка работоспособности

- `GET /health` — общий health; в теге `telegram` проверяется доступность Telegram Bot API.
- `GET /` — перенаправление на `/health`.

## Структура репозитория

| Путь | Назначение |
|------|------------|
| `src/Telegram2VkBot/` | приложение: `ForwardWorker` (polling), `VkApiClient`, `MappingRepository`, health check |
| `Dockerfile` | multi-stage сборка .NET 10, runtime + `curl` для healthcheck |
| `docker-compose.yml` | сервис, порт `8080`, том `data`, healthcheck |
