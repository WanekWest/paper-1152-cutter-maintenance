# Статья 1152: исходный код для GitHub

Опубликованный выпуск данных и кода от 26 октября 2026 года:
https://github.com/WanekWest/paper-1152-cutter-maintenance/releases/tag/1152-2026-10-21-audit1

Полный архив данных находится в приложениях Assets к этому выпуску.

В архиве находятся исходники C#, проекты и тесты .NET, схемы, точные
конфигурации T6, Python-анализ, таблицы и материалы исправления Figure 3.
Содержимое `frozen_source/` и `runtime_config/` сохранено без изменения байтов.

## Как разместить

1. Распакуйте `1152_GITHUB_SOURCE_2026-10-26.zip`. В корне должны быть видны
   `README.md`, `CITATION.cff`, `frozen_source`, `runtime_config` и `analysis`.
   В дерево кода GitHub загружают распакованное содержимое, сохраняя папки.
2. Создайте публичный репозиторий. Можно назвать его
   `paper-1152-cutter-maintenance`. Название является предложением.
   Удобно клонировать пустой репозиторий через GitHub Desktop, скопировать
   в него распакованное содержимое, сделать Commit и Push origin.
   Через сайт используйте Add file → Upload files; за один раз допускается
   до 100 файлов, поэтому загружайте частями, сохраняя относительные пути.
3. Откройте Releases → Draft a new release. Предлагаемый тег: `1152-2026-10-21-audit1`.
   Описание готово в `RELEASE_NOTES.md`.
4. Вложите в Release полный файл `1152_DATA_CODE_RELEASE_2026-10-21.zip` и
   `COMPLETE_RELEASE.sha256`. Файл около 163 МБ не предназначен для обычного
   дерева Git; он уже подготовлен рядом с архивом исходников.
5. Опубликуйте Release и проверьте скачивание полного вложения без входа.
   Передайте ссылку на страницу опубликованного Release в этот чат.
   Для заявки RESS потребуется внести ссылку и версию в форму и четыре DOCX.

Согласованный текст лицензии добавляют правообладатели. Архив не выдаёт
автоматически MIT/CC BY и не приписывает соавторам новое согласие.

## Проверка и зависимости

`python tools/verify_package.py` проверяет состав и SHA-256 файлов.
`python tools/verify_package.py --release ПУТЬ_К_ПОЛНОМУ_ZIP` также проверяет
точный полный архив. Для проверки нужен Python 3.10+ без сторонних пакетов.

Для сборки C# нужен SDK .NET 8.0.424, для тестов — MSTest 4.0.2 из NuGet.
Подробности: `DEPENDENCIES.json`, `docs/REPRODUCIBILITY.md`.
SDK, NuGet-кэш и шрифты не являются частью исходного кода и в ZIP не включены.
Данные исследования модельные; новых расчётов при упаковке не выполнялось.

Официальные инструкции GitHub, проверены 26.10.2026:
- https://docs.github.com/en/repositories/working-with-files/managing-files/adding-a-file-to-a-repository
- https://docs.github.com/en/repositories/releasing-projects-on-github/managing-releases-in-a-repository
- https://docs.github.com/en/repositories/releasing-projects-on-github/about-releases
