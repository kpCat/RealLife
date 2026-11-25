# Геоданные RealLife 0.1.0

Установленный район — Стэучены, Молдова. Покрытие состоит из 182 ячеек, примерно 1,59 × 1,74 км по сторонам. Ограничивающий прямоугольник WGS84 — 47,085539–47,102812° с. ш., 28,851887–28,885792° в. д. Игровой край проходит по контуру ячеек, отмеченному на карте.

## Источники и лицензии

| Источник | Снимок и использование | SHA-256 исходника |
|---|---|---|
| [OpenStreetMap contributors](https://www.openstreetmap.org/copyright), [выгрузка API](https://api.openstreetmap.org/api/0.6/map?bbox=28.849,47.075,28.887,47.108) | Получена 26 ноября 2026 года. Самая поздняя отметка объекта в ответе `2026-11-17T11:35:32Z`. Дороги и планы зданий, [ODbL 1.0](https://opendatacommons.org/licenses/odbl/1-0/) | `b7bad3824ce2fb7b3e62146d0cbe93afb180780e79538d60cd9796337ba0d4fb` |
| [Copernicus GLO-30 Public](https://registry.opendata.aws/copernicus-dem/), [тайл N47 E028](https://copernicus-dem-30m.s3.amazonaws.com/Copernicus_DSM_COG_10_N47_00_E028_00_DEM/Copernicus_DSM_COG_10_N47_00_E028_00_DEM.tif) | Модель поверхности COG с шагом 1″ по широте. [Лицензия GLO-30](https://documentation.dataspace.copernicus.eu/APIs/SentinelHub/Data/DEM/resources/license/License-COPDEM-30.pdf) | `21d87c13931c1e94ded8aaa6f7182ba9e6712be8c4a48ef1f6997a8d2a88f8f2` |
| [NGA EGM2008, PROJ-data](https://cdn.proj.org/us_nga_README.txt), [2,5′](https://cdn.proj.org/us_nga_egm08_25.tif) | Локальное окно 5 × 5 отсчётов из GeoTIFF от 28 января 2020 года, общественное достояние | `adaf5bebe6cabb1274fb3928345ce9b91753ae6f5f10b121d5072060e81fcfba` |
| [Natural Earth admin-0, 1:110m](https://www.naturalearthdata.com/downloads/110m-cultural-vectors/), [GeoJSON v5.1.2](https://raw.githubusercontent.com/nvkelso/natural-earth-vector/v5.1.2/geojson/ne_110m_admin_0_countries.geojson) | Берега и обзорные границы на глобусе, [общественное достояние](https://www.naturalearthdata.com/about/terms-of-use/) | `6866c877d39cba9c357620878839b336d569f8c662d3cfab4cb1dbe2d39c977f` |

© OpenStreetMap contributors. Слои дорог и зданий в `region.json` доступны в машиночитаемом виде под ODbL 1.0. При распространении сохраняйте уведомление, лицензию и условия предоставления этой производной базы. Рельеф и обзорный глобус регулируются лицензиями их источников.

Produced using Copernicus WorldDEM-30 © DLR e.V. 2010-2014 and © Airbus Defence and Space GmbH 2014-2018 provided under COPERNICUS by the European Union and ESA; all rights reserved.

Организации, отвечающие за программу Copernicus по закону или по делегированным полномочиям, не несут ответственности за использование Copernicus WorldDEM-30. Подготовка игрового района не означает одобрения со стороны этих организаций.

## Воспроизведение

Нужен Python 3.12. `rasterio==1.4.4` и `Pillow==11.3.0` используются только при подготовке данных. Загрузите GeoJSON, снимок OSM и DEM по ссылкам выше в свой scratch на `D:\Temp\Codex\RealLife\REALLIFE-002\<run-id>`. Адрес OSM возвращает текущие данные. Для точной версии используйте снимок с указанной суммой. В рабочем checkout он сохранён в локальном `Data/Raw/REALLIFE-002/stauceni.osm`.

В примере `$scratch` — каталог с `stauceni.osm`, `dem.tif` и `world.geojson`. Python и сетевой доступ нужны только для GeoBake.

~~~powershell
python -m pip install --target (Join-Path $scratch 'python') 'rasterio==1.4.4' 'Pillow==11.3.0'
$env:PYTHONPATH = Join-Path $scratch 'python'
python Tools/GeoBake.py --crop-geoid (Join-Path $scratch 'geoid.tif')
python Tools/GeoBake.py --osm (Join-Path $scratch 'stauceni.osm') --dem (Join-Path $scratch 'dem.tif') `
    --geoid (Join-Path $scratch 'geoid.tif') --earth (Join-Path $scratch 'world.geojson') --out Builds/GeoData
./Tools/RealLife.ps1 -Action Build
~~~

При сборке `region.json`, `earth.png` и это уведомление копируются в `RealLife_Data/StreamingAssets/RealLifeGeo`. Пакет `md.stauceni`, формат 1, использует ревизию `d5b49c75649d13668ca1`, вычисленную по нормализованному содержимому и хешам источников. При изменении пакета сохранение остаётся на диске и снова доступно после возврата нужной ревизии.

## Геометрия и ограничения

- `CellKey` версии 1 использует доминирующую ось ECEF. При равенстве приоритет X, Y, Z. Грани 0–5 соответствуют +X, −X, +Y, −Y, +Z, −Z. Оставшиеся координаты по возрастанию номера оси делятся на модуль доминирующей оси. Ячейки полуоткрыты, верхний край грани относится к последней ячейке. Родитель определяется делением X и Y на два. Стэучены занимают дочерние ячейки уровня 16 на грани +Z.
- Каждый общий узел рельефа использует один и тот же отсчёт. К высоте GLO-30 над геоидом EGM2008 прибавляется смещение геоида NGA, затем вычисляется локальная вертикаль WGS84 в едином касательном базисе ENU. Шаг сетки меша меньше исходного шага DEM за счёт интерполяции.
- Рельеф основан на модели поверхности, которая может учитывать крыши и кроны. Дороги упрощены согласно доступным тегам OSM. Конвейер обрабатывает внешние кольца простых ways и исключает мультиполигоны зданий, мосты и тоннели. Интерьеры закрыты. Player получает координаты, типы объектов и параметры геометрии без адресов и данных участников OSM.
