$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$output = Join-Path $root "publish\win-x64"

$sdkList = dotnet --list-sdks
if ($LASTEXITCODE -ne 0 -or -not ($sdkList -match '(?m)^10\.')) {
    Write-Error "Не найден .NET 10 SDK. Установи его с https://dotnet.microsoft.com/download/dotnet/10.0"
    exit 1
}

if (Test-Path -LiteralPath $output) {
    Remove-Item -LiteralPath $output -Recurse -Force
}
New-Item -ItemType Directory -Path $output -Force | Out-Null

Push-Location $root
try {
    dotnet publish ".\App\App.csproj" --configuration Release --runtime win-x64 --self-contained true -p:PublishSingleFile=false --output $output
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish завершился с кодом $LASTEXITCODE"
    }
}
finally {
    Pop-Location
}

# Carry a rig saved in the source tree into the portable game folder.
# A fresh install has no Data folder yet; the game creates it when the rig is saved.
$dataDirectory = Join-Path $root "Data"
if (Test-Path -LiteralPath $dataDirectory) {
    $publishedData = Join-Path $output "Data"
    New-Item -ItemType Directory -Path $publishedData -Force | Out-Null
    Copy-Item -Path (Join-Path $dataDirectory "*") -Destination $publishedData -Recurse -Force
    Write-Host "Сохранённые данные игры скопированы в Data."
}

Write-Host ""
Write-Host "Готово. Скопируй всю папку:"
Write-Host $output
Write-Host "На целевом ПК запусти RimClone.exe."