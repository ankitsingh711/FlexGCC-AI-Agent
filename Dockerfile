# One image serves the React UI and the ASP.NET Core API.

# 1) Build the React UI (Vite writes it into the API project's wwwroot).
FROM node:22-bookworm-slim AS ui
WORKDIR /src/frontend
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY frontend/ ./
RUN npm run build

# 2) Publish the API with the built UI included.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api
WORKDIR /src
COPY backend/ backend/
COPY --from=ui /src/backend/src/CodeReviewAgent.Api/wwwroot backend/src/CodeReviewAgent.Api/wwwroot
RUN dotnet publish backend/src/CodeReviewAgent.Api -c Release -o /out

# 3) Runtime image, running as the image's non-root user.
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=api /out ./
COPY samples/ ./samples/
RUN mkdir -p /app/data && chown "$APP_UID" /app/data
USER $APP_UID

ENV ASPNETCORE_HTTP_PORTS=8080 \
    Samples__Path=/app/samples \
    ConnectionStrings__Reviews="Data Source=/app/data/reviews.db"
EXPOSE 8080
ENTRYPOINT ["dotnet", "CodeReviewAgent.Api.dll"]
