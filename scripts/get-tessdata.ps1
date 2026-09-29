# Descarga los datos de idioma de Tesseract (español e inglés) para el OCR de PDFs escaneados.
# Se guardan en %AppData%\DocsDR\tessdata, donde DOCS-DR los busca automáticamente.
$dest = Join-Path $env:APPDATA 'DocsDR\tessdata'
New-Item -ItemType Directory -Force $dest | Out-Null
foreach ($lang in 'spa', 'eng') {
    $file = Join-Path $dest "$lang.traineddata"
    if (Test-Path $file) { Write-Host "$lang ya existe"; continue }
    Write-Host "Descargando $lang..."
    Invoke-WebRequest "https://github.com/tesseract-ocr/tessdata_fast/raw/main/$lang.traineddata" -OutFile $file
}
Write-Host "Listo: $dest"
