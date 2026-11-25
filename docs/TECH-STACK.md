# Технологический стек и бесплатные зависимости

Исследования от 25.11.2026 рекомендуют Unity 6.3 LTS и URP. При фактическом начале Codex выбирает стабильный патч этой ветки после проверки наличия и совместимости, фиксирует ProjectVersion.txt, manifest.json и packages-lock.json. Не обновляет редактор в середине задачи. Условия Unity Personal проверяются при установке. Исследования не являются испытанием пакетов на нашей машине.

| Этап | Компонент | Назначение |
|---|---|---|
| Начало | Unity 6.3 LTS, URP, C#, Windows x64 (Mono для быстрых итераций) | Основная платформа; конкретный патч и пакеты зафиксировать |
| Начало | Input System, UI Toolkit, штатный 3D, Animation, Audio | Управление, интерфейс, представление |
| Начало | Unity Test Framework, Git, PowerShell, Editor API | Воспроизводимая сборка и короткие проверки |
| Начало | Собственные C#-модули, типизированные DTO/JSON, файловое сохранение | Авторитетное состояние и контент |
| Первые районы | AI Navigation, Addressables (локальные) | Локальная пешая навигация и асинхронные ресурсы |
| Реальная территория | Natural Earth, OSM PBF/Geofabrik, Copernicus DEM, внешний GDAL/PROJ, GeoBake | Глобус, региональная база и геометрия |
| Геометрия | Unity Mesh/MeshData, Clipper2 при необходимости, Blender | Контуры, генерация и нормализация объектов |
| Графика | Poly Haven, ambientCG, отобранные модели Kenney/Quaternius и собственные | Единый художественный стиль |
| Транспорт | Собственный дорожный граф, Splines по потребности, штатная физика | Маршруты, представление и ближнее управление |
| Население | Собственные расписания, Utility AI, логическое движение | Поведение и постоянные личности |
| Масштабирование | Jobs, Burst, Collections по профилированию; SQLite через адаптер | Горячие расчёты и большое хранилище |
| Опционально | Cesium, QGIS, Osmium, NetTopologySuite, VFX Graph, Animation Rigging, MPFB, Steam Audio, Entities, MessagePack | Только при конкретной необходимости и замене без разрушения сохранений |

Пригодные источники и документация из исследований:

- Unity: https://unity.com/releases/editor/qa/lts-releases ; https://docs.unity.com/en-us/engine/6000.3/manual/
- Natural Earth: https://www.naturalearthdata.com/downloads/ ; https://www.naturalearthdata.com/about/terms-of-use/
- OSM: https://www.openstreetmap.org/copyright ; https://download.geofabrik.de/
- Copernicus DEM: https://dataspace.copernicus.eu/explore-data/data-collections/copernicus-contributing-missions/collections-description/COP-DEM
- GDAL: https://gdal.org/en/stable/
- Cesium: https://cesium.com/learn/unity/ ; https://github.com/CesiumGS/cesium-unity
- Blender: https://www.blender.org/
- PBR: https://polyhaven.com/ ; https://ambientcg.com/
- SQLite: https://www.sqlite.org/download.html
- Полный каталог и подтверждения лицензий: research/06-Free-Assets-And-Plugins.md.

Не покупать и не включать обязательные подписные сервисы. Условия использования конкретной версии и ассета проверяются автоматически/по документации перед импортом. Прямая ссылка, версия, лицензия, источник и место использования записываются в реестр стороннего контента. Открытый код плагина не означает открытые права на его облачные данные. Отсутствие обязательной покупки не означает отсутствие времени на подготовку, дискового места и вычислительных затрат.

Новые зависимости проверять ровно настолько, насколько нужно для текущего использования: импорт/компиляция, запуск Windows Player при интеграционном риске, и корректность используемых разрешений. Отклонённые варианты заменять подходящим штатным решением, не блокировать игру.
