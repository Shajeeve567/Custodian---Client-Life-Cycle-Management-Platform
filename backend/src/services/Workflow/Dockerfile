FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY src/shared/Custodian.Shared/ src/shared/Custodian.Shared/
COPY src/shared/Custodian.Shared.Reporting/ src/shared/Custodian.Shared.Reporting/

COPY src/services/Workflow/ src/services/Workflow/

WORKDIR /src/src/services/Workflow
RUN dotnet restore

RUN dotnet publish -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

RUN addgroup --system appgroup && adduser --system --ingroup appgroup appuser
USER appuser

COPY --from=build /app/publish .

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

ENTRYPOINT ["dotnet", "Workflow.dll"]