"""Сводит реестры IEEE в один сжатый файл «префикс MAC -> вендор».

Формат намеренно текстовый: строка на запись, префикс и название через табуляцию.
Двоичный был бы на треть компактнее, но перестал бы читаться глазами и в diff —
а базу, встроенную в поставку, должно быть видно на ревизии.

Длина префикса и есть признак реестра: 6 знаков — MA-L (24 бита),
7 — MA-M (28 бит), 9 — MA-S (36 бит). Отдельного поля для этого не нужно.
"""

import csv
import gzip
import io
import os
import re
import sys

WHITESPACE = re.compile(r"\s+")

REGISTRIES = [
    ("ma-l.csv", 6),
    ("ma-m.csv", 7),
    ("ma-s.csv", 9),
]


def clean(name):
    """Приводит название организации к виду, пригодному для показа в таблице."""
    name = WHITESPACE.sub(" ", name).strip().strip('"')

    # Пустые и служебные записи в реестре встречаются: показывать их незачем.
    if not name or name.lower() in ("private", "ieee registration authority"):
        return None

    return name


def read(path, prefix_length):
    with io.open(path, encoding="utf-8-sig", newline="") as handle:
        for row in csv.DictReader(handle):
            assignment = (row.get("Assignment") or "").strip().upper()
            name = clean(row.get("Organization Name") or "")

            if len(assignment) != prefix_length or not name:
                continue

            if any(c not in "0123456789ABCDEF" for c in assignment):
                continue

            yield assignment, name


def main():
    source, target = sys.argv[1], sys.argv[2]
    entries = {}

    for file_name, prefix_length in REGISTRIES:
        path = os.path.join(source, file_name)
        before = len(entries)

        for assignment, name in read(path, prefix_length):
            entries[assignment] = name

        print("  %-10s %6d записей" % (file_name, len(entries) - before))

    lines = "".join(
        "%s\t%s\n" % (prefix, entries[prefix]) for prefix in sorted(entries)
    ).encode("utf-8")

    # mtime=0: файл должен быть побайтово одинаковым при одинаковых входных данных,
    # иначе каждая пересборка базы давала бы бессмысленный diff.
    with io.open(target, "wb") as handle:
        with gzip.GzipFile(fileobj=handle, mode="wb", compresslevel=9, mtime=0) as gz:
            gz.write(lines)

    print()
    print("  всего        %6d записей" % len(entries))
    print("  распаковано  %6.1f КБ" % (len(lines) / 1024.0))
    print("  в поставке   %6.1f КБ" % (os.path.getsize(target) / 1024.0))


if __name__ == "__main__":
    main()
