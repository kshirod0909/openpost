# syntax=docker/dockerfile:1

# ---------- build ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/OpenPost.Api/OpenPost.Api.csproj src/OpenPost.Api/
RUN dotnet restore src/OpenPost.Api/OpenPost.Api.csproj

COPY src/OpenPost.Api/ src/OpenPost.Api/
WORKDIR /src/src/OpenPost.Api
RUN dotnet publish OpenPost.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

# ---------- runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Storage for raw MIME, attachments and queued outbound mail.
RUN mkdir -p /var/lib/openpost && chown -R app:app /var/lib/openpost

COPY --from=build --chown=app:app /app/publish .

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    Storage__RootPath=/var/lib/openpost \
    MailServer__SmtpPort=2525 \
    MailServer__SubmissionPort=5587

USER app
EXPOSE 8080 2525 5587

ENTRYPOINT ["dotnet", "OpenPost.Api.dll"]
