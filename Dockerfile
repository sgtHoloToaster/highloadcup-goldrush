FROM mcr.microsoft.com/dotnet/sdk:5.0-buster-slim AS build

# build application 
WORKDIR /src
COPY . .
RUN dotnet restore
RUN dotnet publish -c release -o /app --no-self-contained --no-restore

# final stage/image
FROM mcr.microsoft.com/dotnet/runtime:5.0-buster-slim

WORKDIR /app
COPY --from=build /app .

ENV COMPlus_gcServer=1
ENV COMPlus_gcConcurrent=1
ENV COMPlus_GCHeapHardLimitPercent=5A

ENTRYPOINT ["./GoldRush"]