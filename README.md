# DOCS-DR

Lector de PDF y convertidor **PDF → Excel** para Windows (C# / .NET 10 / WPF). Uso interno/personal
(usa MuPDF.NET, licencia AGPL).

## Ejecutar

```powershell
dotnet run --project src/DocsDR.App            # abre la app
dotnet run --project src/DocsDR.App -- archivo.pdf
dotnet test                                    # pruebas
```

Requiere el SDK de .NET 10 (`winget install Microsoft.DotNet.SDK.10`).

## Estado

| Fase | Estado |
|---|---|
| 1 Visor (pestañas, zoom, miniaturas, marcadores, búsqueda, selección y copia de texto, imprimir, contraseña) | Hecha |
| 2 Convertidor PDF → Excel | Hecha (ver abajo) |
| 3 Anotar y organizar páginas | Hecha (ver abajo) |
| 4 Editar texto e imágenes | Hecha (ver abajo) |

## Instalador y versiones

```powershell
dotnet tool install -g vpk                                   # una sola vez
powershell -File scripts/build-installer.ps1 -Version 1.0.0  # genera artifacts/releases
```

Produce `DocsDR-win-Setup.exe` (instalador para el usuario, ~120 MB porque incluye .NET; no necesita nada instalado), `DocsDR-win-Portable.zip` y los paquetes de actualización. Instala por usuario, sin permisos de administrador, con accesos directos en el escritorio y el menú Inicio.

**Publicar una versión** (para que las copias instaladas se actualicen solas):
1. Sube la versión: cambia `-Version` (p. ej. `1.0.1`) y ejecuta el script otra vez. Conviene descargar antes la versión anterior en `artifacts/releases` (`vpk download github --repoUrl https://github.com/DRPCS-D/docs-dr`) para que se generen actualizaciones «delta» pequeñas.
2. Crea un *Release* en GitHub con la etiqueta de la versión y sube **todos** los archivos de `artifacts/releases` (o usa `vpk upload github --repoUrl https://github.com/DRPCS-D/docs-dr --publish --releaseName "DOCS-DR 1.0.1" --tag v1.0.1 --token <TOKEN>`).
3. Las copias instaladas verán la versión nueva en **Ayuda › Buscar actualizaciones**.

El instalador no está firmado: Windows SmartScreen o el antivirus pueden mostrar un aviso la primera vez. Se resuelve con un certificado de firma de código.

## Cinta de opciones

Las funciones están agrupadas en pestañas: **Inicio** (abrir, guardar, imprimir, deshacer, navegación, zoom, búsqueda), **Anotaciones**, **Edición**, **Páginas** y **Convertir** y **Ayuda**. Al cambiar de pestaña se suelta la herramienta activa.

## Ayuda y actualizaciones

- **Ayuda › Acerca de** (`F1`): versión 1.0.0, empresa (DRPCS E.A.S.), autor, soporte, repositorio y licencias de terceros. Los datos están en `Services/AppInfo.cs`.
- **Buscar actualizaciones**: usa [Velopack](https://velopack.io) leyendo las versiones publicadas en GitHub Releases de `DRPCS-D/docs-dr`. Solo funciona en la copia instalada con el instalador; en una copia sin instalar ofrece abrir la página de descargas.
- Soporte por correo, reporte de errores (Issues) y sitio del proyecto.

## Edición del contenido (fase 4, pestaña *Edición*)

- **Editar texto**: clic en un renglón (o, con *Editar párrafo completo*, en un párrafo). Se abre un cuadro sobre el texto con su formato original cargado (fuente, tamaño, negrita/cursiva, color) que puedes cambiar; `Intro` aplica y `Esc` cancela. Solo se borra el texto de esa zona: fondos, líneas e imágenes se conservan. Las cifras se alinean a la derecha por defecto.
- **＋ Texto**: escribe un texto nuevo en cualquier punto de la página (`Ctrl+Intro` aplica).
- **Editar imagen**: clic para seleccionar (contorno y asas), arrastrar para mover, arrastrar una esquina para redimensionar (mantiene proporciones), `Supr` para eliminar, *Reemplazar imagen…* para cambiarla. **＋ Imagen** inserta una nueva.

Limitaciones: la fuente original se sustituye por la equivalente (Arial, Times New Roman o Courier New); un renglón con formatos mezclados se reescribe con el dominante; el texto que no cabe reduce el tamaño hasta un 60 %; no se edita en páginas giradas ni en escaneadas (sin texto); mover o quitar una imagen quita cualquier imagen que toque su caja.

Nota técnica: MuPDF.NET escribe los números del contenido con la cultura del sistema, lo que corrompe colores y posiciones con decimales en configuraciones con coma decimal; todas las operaciones de escritura se ejecutan con cultura invariante (`MuPdfContentEditor.cs`).

## Anotar y organizar (fase 3)

- **Marcado de texto**: resaltar, subrayar, tachar (selecciona texto y la herramienta, o al revés).
- **Anotaciones**: nota adhesiva, cuadro de texto, lápiz, rectángulo, elipse, línea, flecha, sellos (Aprobado, Borrador, Confidencial…, fecha de hoy) e imágenes/firmas. Color y grosor elegibles; el color también recolorea la anotación seleccionada.
- **Seleccionar anotaciones**: clic (doble clic edita su texto), arrastrar mueve notas, textos, sellos y formas cerradas, `Supr` la borra. La pestaña **Notas** lista todas y navega a cada una.
- **Páginas** (menú *Páginas* o clic derecho en las miniaturas): rotar, eliminar, insertar en blanco, insertar desde otro PDF, extraer, dividir (cada N páginas o por rangos) y reordenar arrastrando miniaturas (Ctrl/Mayús para varias). *Archivo › Unir PDFs* combina varios archivos.
- **Guardar** (`Ctrl+S`, `Ctrl+Mayús+S`), **deshacer/rehacer** (`Ctrl+Z`/`Ctrl+Y`, hasta 30 pasos) y aviso al cerrar con cambios. Los PDF con contraseña conservan su protección.

Deshacer se apoya en instantáneas del documento completo: es lo más fiable, pero en PDF muy grandes cada edición tarda más.

## Convertidor PDF → Excel (`Ctrl+E`)

1. **Detectar**: con bordes (rejilla de líneas vectoriales o, en escaneados, líneas halladas en la imagen) y sin bordes (alineación de columnas).
2. **Ajustar a mano**: dibujar tabla, mover/redimensionar, agregar/mover/quitar separadores de columna y fila (clic derecho quita), filas de encabezado, «aplicar a páginas 2-5».
3. **Multipágina**: las tablas consecutivas con las mismas columnas se unen y se quitan los encabezados repetidos (se puede forzar unir / no unir).
4. **Limpieza**: números `1.234,56` / `1,234.56`, negativos `(x)` o `x-`, moneda, %, fechas es-ES/es-CO, unir filas partidas, códigos con ceros a la izquierda se conservan como texto.
5. **Exportar**: `.xlsx` con celdas tipadas (una hoja por tabla o consolidado) o CSV.

### OCR (PDFs escaneados)

Usa el Tesseract integrado de MuPDF. Descarga los idiomas una vez:

```powershell
powershell -File scripts/get-tessdata.ps1
```

## Estructura

- `DocsDR.Core`: modelos y contratos (`IPdfDocument`, `TableDefinition`…).
- `DocsDR.Pdf`: adaptador MuPDF.NET (único proyecto que toca MuPDF).
- `DocsDR.TableExtraction`: detectores, unión multipágina, limpieza.
- `DocsDR.Excel`: exportación XLSX/CSV.
- `DocsDR.App`: WPF (visor y ventana del convertidor).
