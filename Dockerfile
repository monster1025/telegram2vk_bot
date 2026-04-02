FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ./src ./src

RUN dotnet restore "src/Telegram2VkBot/Telegram2VkBot.csproj"
RUN dotnet publish "src/Telegram2VkBot/Telegram2VkBot.csproj" -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/runtime:8.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "Telegram2VkBot.dll"]

