"""Regenerate Windows11Fonts.tsv with Python 3.10+, standard library only.

From the repository root, save the en-US Learn page as HTML (not as Markdown):
    Invoke-WebRequest 'https://learn.microsoft.com/en-us/typography/fonts/windows_11_font_list' -OutFile <saved-html>
    py -3.10 workbench/tools/generate_windows11_fonts.py --check --html <saved-html>
    py -3.10 workbench/tools/generate_windows11_fonts.py --write --html <saved-html>
Or use --fetch in place of --html to read the live page. Use --output to stage
the candidate elsewhere. Review the row diff, section counts, page dates, and
any now-obsolete aliases before updating the pinned Management.Checks counts
and digest. This tool never generates or changes the curated alias resource.
"""

import argparse
from collections import Counter
from dataclasses import dataclass
import hashlib
from html.parser import HTMLParser
from pathlib import Path
import re
from urllib.request import Request, urlopen


SOURCE = "https://learn.microsoft.com/en-us/typography/fonts/windows_11_font_list"
OFFICE = "https://aka.ms/OfficeFonts"
SNAPSHOT = Path(__file__).resolve().parents[1] / "management" / "Windows11Fonts.tsv"
COLUMNS = ("Family", "Font Name", "File Name", "Version")
FONT_FILE = re.compile(r"[A-Za-z0-9_. -]+\.(?:ttf|otf|ttc|otc|fon|fnt)", re.IGNORECASE)


@dataclass(frozen=True)
class Cell:
    tag: str
    text: str
    rowspan: int


class LearnTables(HTMLParser):
    """Collect the document's tables, including physical cell spans."""

    def __init__(self):
        super().__init__()
        self.in_main = False
        self.canonical = None
        self.meta = {}
        self.heading = ""
        self.office_link = False
        self.tables = []
        self.table = None
        self.row = None
        self.cell = None

    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        if tag == "main":
            self.in_main = True
        elif tag == "meta" and "name" in attrs and "content" in attrs:
            self.meta[attrs["name"]] = attrs["content"]
        elif tag == "link" and attrs.get("rel") == "canonical":
            self.canonical = attrs.get("href")
        elif not self.in_main:
            return
        elif tag == "h2":
            self.heading = attrs.get("id", "")
        elif tag == "a" and self.heading == "see-also" and attrs.get("href") == OFFICE:
            self.office_link = True
        elif tag == "table":
            if self.table is not None:
                raise ValueError("Nested font table")
            self.table = []
        elif tag == "tr" and self.table is not None:
            if self.row is not None:
                raise ValueError("Unclosed font table row")
            self.row = []
        elif tag in ("td", "th") and self.row is not None:
            rowspan = int(attrs.get("rowspan", "1"))
            if self.cell is not None or rowspan < 1 or attrs.get("colspan", "1") != "1":
                raise ValueError("Unsupported font table cell layout")
            self.cell = (tag, rowspan, [[]])
        elif tag == "br" and self.cell is not None:
            self.cell[2].append([])

    def handle_data(self, data):
        if self.cell is not None:
            self.cell[2][-1].append(data)

    def handle_endtag(self, tag):
        if tag in ("td", "th") and self.cell is not None:
            cell_tag, rowspan, parts = self.cell
            if tag != cell_tag:
                raise ValueError("Mismatched font table cell tag")
            text = ", ".join(" ".join("".join(part).split()) for part in parts)
            self.row.append(Cell(tag, text, rowspan))
            self.cell = None
        elif tag == "tr" and self.row is not None:
            if self.cell is not None:
                raise ValueError("Unclosed font table cell")
            self.table.append(self.row)
            self.row = None
        elif tag == "table" and self.table is not None:
            if self.row is not None:
                raise ValueError("Unclosed font table row")
            self.tables.append((self.heading, self.table))
            self.table = None
        elif tag == "main":
            if self.table is not None:
                raise ValueError("Unclosed font table")
            self.in_main = False


def expand_rows(section, rows):
    active = {}
    for number, raw in enumerate(rows, 1):
        cells = {column: cell for column, (cell, _) in active.items()}
        active = {column: (cell, left - 1) for column, (cell, left) in active.items() if left > 1}
        column = 0
        for cell in raw:
            while column in cells:
                column += 1
            cells[column] = cell
            if cell.rowspan > 1:
                active[column] = (cell, cell.rowspan - 1)
        if not cells:
            continue  # The source has empty structural <tr>s after headers.
        if set(cells) != set(range(len(COLUMNS))):
            raise ValueError(f"{section}: expected four cells on table row {number}")
        yield tuple(cells[column] for column in range(len(COLUMNS)))
    if active:
        raise ValueError(f"{section}: rowspan extends past the end of the table")


def file_names(text):
    if text.casefold() in ("", "-", "\u2013", "\u2014", "n/a"):
        return ""
    names = re.split(r"\s*[,;&+/]\s*", text)
    if any(not FONT_FILE.fullmatch(name) for name in names):
        raise ValueError(f"Unrecognized File Name cell: {text!r}")
    return ", ".join(names)


def extract_rows(heading, cells):
    section = "system" if heading == "introduction" else heading
    if section != "system" and not section.endswith("-supplemental-fonts"):
        raise ValueError(f"Unclassified font table section: {heading!r}")
    rows = iter(expand_rows(section, cells))
    header = next(rows, ())
    if tuple(cell.text for cell in header) != COLUMNS or any(cell.tag != "th" for cell in header):
        raise ValueError(f"{section}: unexpected font table header")
    family = ""
    data = []
    for row in rows:
        label, face, files, version = (cell.text for cell in row)
        family = label or family
        if any(cell.tag != "td" for cell in row) or not family or not face:
            raise ValueError(f"{section}: missing family or font name or unexpected header: {row!r}")
        # Empty family cells continue the previous group, but empty file cells never do.
        data.append((section, family, face, file_names(files), version))
    if not data:
        raise ValueError(f"{section}: empty font table")
    return data


def generate(html):
    page = LearnTables()
    page.feed(html)
    page.close()
    if page.canonical != SOURCE or not page.office_link:
        raise ValueError("Expected the English Windows 11 list and its Office cloud-font cross-reference")

    rows = [row for heading, cells in page.tables for row in extract_rows(heading, cells)]
    sections = Counter(row[0] for row in rows)
    if len(sections) != len(page.tables) or "system" not in sections or len(sections) < 2:
        raise ValueError("Missing system/supplemental tables or repeated table section")
    families = {row[1].lower() for row in rows}
    files = {file.lower() for row in rows if row[3] for file in row[3].split(", ")}
    missing_files = sum(not row[3] for row in rows)
    body = "".join("\t".join(row) + "\n" for row in rows)
    digest = hashlib.sha256(body.encode("utf-8")).hexdigest()
    header = [
        "# Generated by workbench/tools/generate_windows11_fonts.py; do not edit by hand.",
        f"# source: {SOURCE}",
        f"# ms.date: {page.meta['ms.date']}",
        f"# updated_at: {page.meta['updated_at']}",
        f"# source_revision: {page.meta['gitcommit']}",
        f"# tables: {len(sections)}; rows: {len(rows)}; families: {len(families)}; unique_files: {len(files)}",
        f"# rows_without_file_names: {missing_files}",
        "# section_rows: " + "; ".join(f"{section}={count}" for section, count in sections.items()),
        f"# rows_sha256_utf8_lf: {digest}",
        f"# scope: Office cloud fonts ({OFFICE}) are a See Also link, not a table. No other app catalogue is imported.",
        "# source_notes: * marks a Windows 11 addition. Family groups are filled down; file lists use comma-space.",
        "# source_notes: File/name discrepancies in the source are preserved; reviewed alternatives are in Windows11FontAliases.tsv.",
        "section\tfamily\tfont_name\tfile_names\tversion",
    ]
    return "\n".join(header) + "\n" + body


def self_test():
    parser = LearnTables()
    parser.feed("""<main><h2 id="introduction">Introduction</h2><table>
      <tr><th>Family</th><th>Font Name</th><th>File Name</th><th>Version</th></tr><tr></tr>
      <tr><td rowspan="3"><a>Fixture &amp; Co</a></td><td>First *</td>
          <td rowspan="2">A.ttf, B.ttc<br>C Italic.ttf &amp; D.otf</td><td>1.00</td></tr>
      <tr><td>Second</td><td>1.01</td></tr>
      <tr><td>Third</td><td>&mdash;</td><td>1.02</td></tr>
      <tr><td></td><td>Fourth</td><td></td><td>1.03</td></tr>
    </table></main>""")
    expected = [
        ("system", "Fixture & Co", "First *", "A.ttf, B.ttc, C Italic.ttf, D.otf", "1.00"),
        ("system", "Fixture & Co", "Second", "A.ttf, B.ttc, C Italic.ttf, D.otf", "1.01"),
        ("system", "Fixture & Co", "Third", "", "1.02"),
        ("system", "Fixture & Co", "Fourth", "", "1.03"),
    ]
    if len(parser.tables) != 1 or extract_rows(*parser.tables[0]) != expected:
        raise AssertionError("Span, combined filename, or blank-cell fixture failed")
    try:
        file_names("A.ttf, unknown")
    except ValueError:
        pass
    else:
        raise AssertionError("Unknown filename syntax was accepted")
    print("PASS: generator rowspan, combined files, missing filenames, and invalid filename fixtures")


def main():
    cli = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    mode = cli.add_mutually_exclusive_group(required=True)
    mode.add_argument("--write", action="store_true", help="write the generated snapshot")
    mode.add_argument("--check", action="store_true", help="compare every row and metadata against the snapshot")
    mode.add_argument("--self-test", action="store_true", help="exercise the parser without a source or network")
    source = cli.add_mutually_exclusive_group()
    source.add_argument("--html", type=Path, help="read a locally saved Microsoft Learn HTML page")
    source.add_argument("--fetch", action="store_true", help="fetch the en-US Microsoft Learn HTML page")
    cli.add_argument("--output", type=Path, default=SNAPSHOT, help="snapshot path to write or compare")
    args = cli.parse_args()
    if args.self_test:
        self_test()
        return
    if not args.html and not args.fetch:
        cli.error("--write and --check require either --html or --fetch")

    try:
        if args.html:
            html = args.html.read_text(encoding="utf-8-sig")
        else:
            with urlopen(Request(SOURCE, headers={"Accept": "text/html"}), timeout=30) as response:
                html = response.read().decode("utf-8-sig")
        generated = generate(html)
        if args.check:
            if args.output.read_text(encoding="utf-8") != generated:
                cli.exit(1, f"Snapshot differs from source: {args.output}\n")
            print(f"PASS: {args.output} matches all source rows and metadata")
        else:
            args.output.write_bytes(generated.encode("utf-8"))
            print(f"Wrote {args.output}")
    except (OSError, ValueError, KeyError) as error:
        cli.exit(1, f"Cannot update Windows 11 font snapshot: {error}\n")


if __name__ == "__main__":
    main()
