# PrehistorikSharp2: port nativo en C#

Port de *Prehistorik 2* (1993) a C# con raylib, reconstruido a partir del port
de referencia [pre2_port](https://github.com/missingno7/pre2_port) y verificado
contra el ejecutable original.

**No es un remake ni un emulador**: el juego corre como código nativo, sin
intérprete x86 y sin cargar `PRE2.EXE` en tiempo de ejecución. El ejecutable
original solo se usa una vez, durante la instalación, para reconstruir el estado
de arranque del juego.

> Requiere tu propia copia de *Prehistorik 2*. Este programa **no incluye los
> datos del juego**.

Si te gusta el juego, podés comprarlo aquí:

- **GOG**: https://www.gog.com/en/game/prehistorik_12
- **Steam**: https://store.steampowered.com/app/749140/Prehistorik_2/

![C#](https://img.shields.io/badge/C%23-.NET%2010-512BD4?logo=dotnet&logoColor=white)
![raylib](https://img.shields.io/badge/raylib-6.0-000000?logo=raylib&logoColor=white)
![Plataforma](https://img.shields.io/badge/plataforma-Windows-0078D4?logo=windows&logoColor=white)
![Datos](https://img.shields.io/badge/datos-juego%20original-orange)

---

## Índice

- [Características](#características)
- [Primer inicio](#primer-inicio)
- [Controles](#controles)
- [Configuración](#configuración)
- [Compilar](#compilar)
- [Verificación](#verificación)
- [Registro de errores](#registro-de-errores)
- [Créditos y fuentes](#créditos-y-fuentes)
- [Diferencias con el original](#diferencias-con-el-original)
- [Estructura del proyecto](#estructura-del-proyecto)

---

## Características

- **Port nativo en C#** de todo el juego: menús, mapa, contraseñas, los 10
  niveles principales, niveles de bonus por warp, tally, jefes, pantallas de fin
  y demo de atracción.
- **No es emulación**: la lógica, el render VGA y el audio están reimplementados en
  C#; el `dos_re` original se conserva únicamente como oráculo de verificación.
- **Primer inicio guiado**: el launcher busca la copia original, valida cada
  archivo y prepara la carpeta `files\` automáticamente.
- **Ventana 960x600 (3x)** o pantalla completa, con modo **Original** y
  **WideScreen** (más área visible del nivel).
- **Dibujo a 60 fps o más** (hasta 360 fps configurable) con interpolación de
  sprites, fondo, cámara y paleta, manteniendo la lógica original a **70/3 ticks
  por segundo**.
- **Filtros gráficos**: Bilinear, Sharp Bilinear, FSR, xBR, Scale3x, Vibrance y
  Scanlines.
- **Opciones en el juego** (tecla `ESC`): vidas, volumen, FPS, V-Sync, filtros y
  reasignación de teclas y botones del gamepad.
- **Audio digital**: reproductor de módulos ProTracker y mezclador de efectos
  recuperados del original, con volumen ajustable en vivo.
- **Modo ventana y pantalla completa**, V-Sync, contador de FPS y filtro de
  pulsaciones repetidas.
- **Registro de errores** detallado en `log_error.txt`.

---

## Primer inicio

1. Ejecutá `Pre2Port.exe`.
2. En la pantalla de bienvenida, elegí **EXAMINAR** y seleccioná la carpeta
   donde tenés instalado *Prehistorik 2*.
3. El programa busca los archivos necesarios (también en subcarpetas, hasta dos
   niveles de profundidad), los copia a `files\` junto al ejecutable y valida
   tamaño y SHA-256 de cada uno.
4. Cuando aparece **LISTO**, el juego arranca.

En los siguientes inicios el port lee directamente de `files\`


## Controles

### Juego

| Tecla | Acción |
|---|---|
| Flechas izquierda / derecha | Caminar |
| Flecha arriba | Saltar |
| Flecha abajo | Agacharse |
| Espacio | Atacar / usar / interactuar |
| `ESC` | Pausa y menú de opciones |
| `Enter` / `Espacio` | Confirmar en menús |
| Flechas en menús | Navegar y cambiar valores |
| `ESC` en un submenú | Volver atrás |

### Gamepad

| Entrada | Acción por defecto |
|---|---|
| D-pad / stick izquierdo | Mover y saltar |
| Botón cuadrado (PS) / X (Xbox) | Fuego |
| Botón cruz (PS) / A (Xbox) | Salto |
| Start | Pausa / opciones |

Los botones de fuego y salto, y todas las teclas de acción, se pueden reasignar
desde el menú **Controller**. Las teclas `ESC` y `Enter` están reservadas.

> En el juego original `ESC` cerraba el programa; en este port pausa la partida
> y abre el menú de opciones. Para salir hay que elegir **Exit to Windows** en
> ese menú.

---

## Configuración

El port usa dos archivos junto al ejecutable. Se crean solos en el primer inicio
y se pueden editar desde el menú de opciones; los cambios de opciones se aplican
al instante.

### `Prehistorik2.cfg`

| Clave | Valores | Descripción |
|---|---|---|
| `FULLSCREEN` | `NO`, `ORIGINAL`, `WIDESCREEN` | Ventana 960x600, pantalla completa 4:3 o pantalla completa ampliada |
| `FPS` | `1` a `320` | Frecuencia de dibujo (por defecto `60`) |
| `FILTERSCALING` | `OFF`, `BILINEAR`, `SHARPBILINEAR`, `FSR` | Escalado de imagen |
| `FILTERPIXELART` | `OFF`, `XBR`, `SCALE3X` | Realce de pixel art |
| `FILTEREFFECTS` | `OFF`, `VIBRANCE`, `SCANLINES` | Efectos de color y líneas de barrido |
| `Vsync` | `ON`, `OFF` | Sincronización vertical (por defecto `OFF`) |
| `LivesAtStartup` | `3` a `10` | Vidas iniciales (por defecto `3`) |
| `Volume` | `0` a `100` | Volumen general (por defecto `100`) |
| `ShowFps` | `YES`, `NO` | Mostrar contador de FPS (solo en pantalla completa) |

Notas:

- La lógica del juego siempre corre a **70/3 Hz** (unos 23,33 ticks por
  segundo); `FPS` solo cambia la frecuencia de dibujo.
- El modo `WIDESCREEN` amplía el área visible en niveles, mapa, menú, tally y
  pantallas finales. La foto de los creadores se mantiene en 640x480.
- Las claves no distinguen mayúsculas de minúsculas.

### `Controller.cfg`

| Clave | Valores | Descripción |
|---|---|---|
| `Mode` | `Keyboard`, `Joystick` | Dispositivo activo |
| `Left` / `Right` / `Down` / `Jump` / `Fire` | Nombre de tecla | Teclas de cada acción |
| `Joystick` | `0`, `1`, … | Ranura del gamepad |
| `PadFire` | Botón de gamepad | Botón de fuego |
| `PadJump` | Botón de gamepad | Botón de salto |

Valores por defecto: flechas, `Jump = Up`, `Fire = Space`; en gamepad,
`Fire = RightFaceLeft` (cuadrado/X) y `Jump = RightFaceDown` (cruz/A).

## Compilar

Requiere **.NET 10 SDK** en Windows (el launcher usa el diálogo de carpetas de
WinForms).

```powershell
cd src
dotnet build -c Release
dotnet run -c Release
```

El ejecutable queda en:

```text
src\bin\Release\net10.0\
```

Para distribuir, copiá **la carpeta completa** (el `.exe` necesita el `.dll`,
`runtimeconfig.json` y las dependencias que están junto a él).

Publicación autocontenida opcional:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true
```

---

## Registro de errores

Si el juego se cierra por una excepción, escribe `log_error.txt` junto al
ejecutable. El registro incluye fecha, versión de .NET, sistema operativo,
arquitectura, línea de comandos, escena, tick, frame, FPS y el estado del juego
en el momento del fallo. Se agrega un bloque por cada error; no se escribe nada
si todo funciona.

---

## Créditos y fuentes

- **Prehistorik 2** (1993), Titus Interactive: juego original y todos sus datos.
- **[pre2_port](https://github.com/missingno7/pre2_port)** (missingno7): port de
  referencia en Python, lógica recuperada y metodología de verificación.
- **[dos_re](https://github.com/missingno7/dos_re)**: VM 8086 usada como oráculo
  offline para validar el comportamiento original.
- **raylib 6.0** y **Raylib-cs 8.1.0** (zlib): ventana, entrada, audio y shaders.
- **.NET 10** (MIT).
- **xBR** (Hyllian): filtro de escalado de pixel art.
- **Scale3x / AdvMAME3x** (Andrea Mazzoleni): filtro de escalado.
- **FSR 1.0** (AMD): EASU + RCAS para el escalado espacial.

---

## Diferencias con el original

### Presentación

- **Ventana y pantalla completa**: se abre en 960x600 (escala 3x entera). El
  original corría a 320x200. Pantalla completa opcional en modo 4:3 o
  WideScreen.
- **WideScreen**: muestra más área del nivel y de las pantallas anchas, sin
  deformar los gráficos.
- **Alta frecuencia de dibujo**: la lógica sigue a 70/3 ticks por segundo, pero
  el dibujo va a 60 fps por defecto (hasta 1000), con interpolación de posición,
  cámara, paleta y efectos.
- **Filtros en shaders**: Bilinear, Sharp Bilinear, FSR, xBR, Scale3x, Vibrance
  y Scanlines, configurables desde el menú.

### Jugabilidad

- **Pausa y opciones**: `ESC` pausa la partida y abre un menú superpuesto con
  opciones de juego, sonido, controles y gráficos. En el original `ESC` cerraba
  el programa.
- **Menú de opciones nuevo**: vidas iniciales, volumen, FPS, V-Sync, filtros,
  dispositivo de entrada y reasignación de controles, todo en vivo.
- **Vidas configurables**: de 3 a 10 al empezar (el original arranca con 3).
- **Nivel inicial oculto**: la clave `level` en `Prehistorik2.cfg` permite
  empezar una partida nueva en cualquier nivel principal.
- **Filtro de pulsaciones**: una pulsación física cuenta una sola vez, evitando
  dobles registros en menús y pausa.
- **Gamepad**: soporte de D-pad, stick y botones reasignables.
- **Soporte de ventana**: el juego se puede minimizar, cambiar a pantalla
  completa y ejecutar con V-Sync.

### Audio y datos

- **Audio nativo**: reproductor de módulos ProTracker, mezclador de voces y
  efectos recuperado del original, con salida por raylib.
- **Volumen general**: de 0 a 100, aplicado en vivo.
- **Instalación automática**: los archivos `.SQZ`/`.TRK` se copian y validan una
  vez a `files\`; después el juego lee solo de esa carpeta.
- **Sin dependencia del ejecutable**: `PRE2.EXE` solo se usa para generar
  `boot_image.bin`; una vez instalado, el juego no lo ejecuta.
- **Acepta `PRE2.SQZ`**: si la copia original trae el ejecutable comprimido, se
  descomprime automáticamente.
- **Migración de configuración**: `Prehistorik.cfg` se importa una sola vez.


## Estructura del proyecto

```text
src/
├── Codecs/        Descompresores .SQZ / .TRK (LZSS, LZW, Huffman+RLE)
├── Native/        Imagen de arranque, tablas, VGA y estado de nivel
├── Recovered/     Lógica recuperada: jugador, objetos, render, audio, jefes
├── Game/
│   ├── Frontend/  Intro, menús, mapa, contraseñas, demo, pantallas de fin
│   ├── Launcher/  Instalación y primera ejecución
│   ├── Level/     Escena de nivel, HUD, verificación
│   └── Options/   Configuración, controles, filtros y overlay de pausa
├── Program.cs     Arranque y comandos de verificación
└── Pre2Port.csproj
```

---

## Estado

El port cubre la partida completa: front-end, los 10 niveles principales,
niveles de bonus por warp, jefes, sistema de puntuación, contraseñas, demo de
atracción y pantallas finales. Cada subsistema se contrasta contra el comportamiento del
ejecutable original mediante las referencias y sondas del proyecto.

---

*Prehistorik 2 es una marca de sus respectivos propietarios. Este proyecto no
distribuye ni incluye los datos del juego original.*
