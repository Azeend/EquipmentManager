FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY EquipmentManager.csproj ./
RUN dotnet restore EquipmentManager.csproj
COPY Components/ Components/
COPY Models/ Models/
COPY Services/ Services/
COPY Validators/ Validators/
COPY wwwroot/ wwwroot/
COPY Program.cs appsettings.json ./
RUN dotnet publish EquipmentManager.csproj -c Release --no-restore -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0-bookworm-slim AS final
# Публичный CA: проверяем отпечаток, назначение, срок и подпись до добавления в доверенные.
COPY certificates/supabase-ca.crt /usr/local/share/ca-certificates/supabase-ca.crt
RUN test "$(grep -c '^-----BEGIN CERTIFICATE-----$' /usr/local/share/ca-certificates/supabase-ca.crt)" = 1 \
    && ! grep -q 'PRIVATE KEY' /usr/local/share/ca-certificates/supabase-ca.crt \
    && test "$(openssl x509 -in /usr/local/share/ca-certificates/supabase-ca.crt -outform DER | sha256sum | cut -d ' ' -f 1)" = "807025ad50d4ed219d2c9c7d299c004f824eb00cf7f65afef607d07b72e6cafa" \
    && openssl x509 -in /usr/local/share/ca-certificates/supabase-ca.crt -noout -checkend 0 \
    && openssl x509 -in /usr/local/share/ca-certificates/supabase-ca.crt -noout -ext basicConstraints | grep -q 'CA:TRUE'
RUN openssl verify -check_ss_sig -CAfile /usr/local/share/ca-certificates/supabase-ca.crt /usr/local/share/ca-certificates/supabase-ca.crt \
    && chmod 644 /usr/local/share/ca-certificates/supabase-ca.crt \
    && update-ca-certificates \
    && openssl verify -CAfile /etc/ssl/certs/ca-certificates.crt /usr/local/share/ca-certificates/supabase-ca.crt
WORKDIR /app
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://0.0.0.0:10000
EXPOSE 10000
COPY --from=build /app/publish ./
USER $APP_UID
ENTRYPOINT ["dotnet", "EquipmentManager.dll"]
