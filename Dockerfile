# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY MPRSystem.sln .
COPY MPR.Domain/MPR.Domain.csproj MPR.Domain/
COPY MPR.Application/MPR.Application.csproj MPR.Application/
COPY MPR.Infrastructure/MPR.Infrastructure.csproj MPR.Infrastructure/
COPY MPR.Web/MPR.Web.csproj MPR.Web/
RUN dotnet restore MPRSystem.sln

COPY . .
RUN dotnet publish MPR.Web/MPR.Web.csproj -c Release -o /app/publish

# Runtime stage - installs the native binaries the app depends on that NuGet alone
# doesn't provide: Tesseract OCR engine + its language data, and the native libs
# OpenCvSharp's Linux runtime package needs at runtime.
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

RUN apt-get update && apt-get install -y --no-install-recommends \
    tesseract-ocr \
    tesseract-ocr-eng \
    libgdiplus \
    libglib2.0-0 \
    libsm6 \
    libxext6 \
    libxrender1 \
    && rm -rf /var/lib/apt/lists/*

# Tesseract's apt package installs trained data under /usr/share/tesseract-ocr/*/tessdata -
# point the app's Ocr:TessDataPath there via env var (set in render.yaml) rather than
# the App_Data path used for local Windows dev.
ENV OCR__TESSDATAPATH=/usr/share/tesseract-ocr/5/tessdata

COPY --from=build /app/publish .

# Render sets $PORT itself; Program.cs reads it and binds to 0.0.0.0:$PORT.
EXPOSE 8080

ENTRYPOINT ["dotnet", "MPR.Web.dll"]
