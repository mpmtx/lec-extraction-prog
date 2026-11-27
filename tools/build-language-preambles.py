"""Build the pipeline's per-language preambles from the two book preambles.

    pdf-preamble-de.tex  <- the current Grundstrukturen book, spring-2025 (German)
    pdf-preamble-en.tex  <- Algebra 1 book preamble (English)

Each output is the source's preamble (everything before \\begin{document}) plus what the pipeline's
prompts teach but the book does not define yet: the `observation` theorem environment,
`\\checkline`, `\\setSpeaker` and `\\youtubechapter`; and questions/answers recoloured to the green of `\\recap`, the
answer with its own icon. A book's own `\\setSpeaker{...}` (its lecturer) is dropped. Rerun after
either book preamble changes:

    py tools/build-language-preambles.py

The Algebra book's main file was renamed from algebra-1.tex to algebra-1-transcription.tex on
2026-11-24. Since 2026-11-27 that book's running header is meant for the book only ("W11 | Tue,
24.11.2026 | Title" on one line, \\footnotesize); a rerun copies it into pdf-preamble-en.tex, so ask
the maintainer before regenerating.
"""
import os
import re
import sys

BS = chr(92)
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
LATEX = r"C:\Users\miche\latex"

SOURCES = {
    "de": (os.path.join(LATEX, "grundstrukturen-ai-transcription-spring-2025", "grundstrukturen.tex"),
           "Beobachtung", "Beobachtungen", "Bemerkung"),
    "en": (os.path.join(LATEX, "algebra-1-ai-transcription-fall-2026", "algebra-1-transcription.tex"),
           "Observation", "Observations", "Remark"),
}

SPEAKER = r"""
% Speaker of the following speech blocks (guest lecturer, change mid-lecture); \setSpeaker{} clears it.
% Shown faintly after the timestamp; without \setSpeaker nothing is shown.
\providecommand{\lecturespeaker}{}
\providecommand{\setSpeaker}[1]{\renewcommand{\lecturespeaker}{#1}}
\providecommand{\speakertag}{\ifdefempty{\lecturespeaker}{}{\quad{\mdseries\footnotesize\color{black!45}\faUser\ \lecturespeaker}}}
"""

YOUTUBE_CHAPTER = r"""
% Custom YouTube / Standalone Video Chapter Command:
% \youtubechapter[Date]{Creator / Channel}{Video Title}
\NewDocumentCommand{\youtubechapter}{ o m m }{%
  \IfValueTF{#1}{%
    \ifblank{#1}{%
      \chapter[\texorpdfstring{--- \color{ThemePrimary!95!black}#2: #3}{--- #2: #3}]{{\Large\color{ChapSubtitleText}#2} \texorpdfstring{\\[1ex]}{--- } {\LARGE #3}}%
      \markboth{\chaptername\ \thechapter\ \ ---\ \ #2\ \ ---\ \ #3}{}%
    }{%
      \chapter[\texorpdfstring{--- \color{ThemePrimary!95!black}#2 (#1): #3}{--- #2 (#1): #3}]{{\Large\color{ChapSubtitleText}#2\ ---\ #1} \texorpdfstring{\\[1ex]}{--- } {\LARGE #3}}%
      \markboth{\chaptername\ \thechapter\ \ ---\ \ #2\ (#1)\ \ ---\ \ #3}{}%
    }%
  }{%
    \chapter[\texorpdfstring{--- \color{ThemePrimary!95!black}#2: #3}{--- #2: #3}]{{\Large\color{ChapSubtitleText}#2} \texorpdfstring{\\[1ex]}{--- } {\LARGE #3}}%
    \markboth{\chaptername\ \thechapter\ \ ---\ \ #2\ \ ---\ \ #3}{}%
  }%
}

"""


def insert_after(lines, anchor, new_lines):
    for i, line in enumerate(lines):
        if line.startswith(anchor):
            lines[i + 1:i + 1] = new_lines
            return
    sys.exit(f"anchor not found: {anchor}")


def insert_before(lines, anchor, new_lines):
    for i, line in enumerate(lines):
        if line.startswith(anchor):
            lines[i:i] = new_lines
            return
    sys.exit(f"anchor not found: {anchor}")


def build(lang, source, obs, obs_plural, remark):
    text = open(source, encoding="utf-8").read().replace("\r\n", "\n")
    # the first \begin{document} at the start of a line: since the vertical-rhythm rollout
    # (2026-11-2x) both book preambles mention \begin{document} in a comment further up
    m = re.search(r"(?m)^" + re.escape(BS + "begin{document}"), text)
    if not m:
        sys.exit(f"no \\begin{{document}} in {source}")
    cut = m.start()
    lines = text[:cut].rstrip("\n").split("\n")
    # a book may set its lecturer once in the preamble; the pipeline's lectures have other speakers,
    # so drop that call together with the comment lines right above it
    for i in [i for i, l in enumerate(lines) if l.startswith(BS + "setSpeaker{")][::-1]:
        start = i
        while start > 0 and lines[start - 1].startswith("%"):
            start -= 1
        del lines[start:i + 1]
    body = "\n".join(lines)

    if BS + "newtheorem{observation}" not in body:
        insert_after(lines, BS + "newtheorem{remark}[theorem]{" + remark + "}",
                     [BS + "newtheorem{observation}[theorem]{" + obs + "}"])
        insert_after(lines, BS + "newtheorem*{remark*}{" + remark + "}",
                     [BS + "newtheorem*{observation*}{" + obs + "}"])
        insert_after(lines, BS + "Crefname{remark}",
                     [BS + "crefname{observation}{" + obs + "}{" + obs_plural + "}",
                      BS + "Crefname{observation}{" + obs + "}{" + obs_plural + "}"])
    if BS + "checkline" not in body:
        # same layout and color as \recap's line, its own check icon
        label = "Probe" if lang == "de" else "Check"
        insert_after(lines, BS + "newcommand{" + BS + "recap}",
                     [BS + "NewDocumentCommand{" + BS + "checkline}{ O{" + label + "} m }{" + BS + "par" + BS + "smallskip"
                      + BS + "noindent{" + BS + "sffamily" + BS + "bfseries" + BS + "color{EnvRemark}" + BS + "faCheckCircle"
                      + BS + " #1:}" + BS + " #2" + BS + "par" + BS + "smallskip}"])
    if "answerthm" not in body:
        # questions and answers in the green of \recap; the answer gets its own icon instead of the
        # question mark. Both stay numbered (shared theorem counter), so either can be referenced.
        insert_after(lines, BS + "colorlet{EnvQuestion}",
                     [BS + "colorlet{EnvQuestion}{EnvRemark}            % pipeline: questions/answers green like " + BS + "recap"])
        insert_after(lines, BS + "NewIconThmStyle{questionthm}",
                     [BS + "NewIconThmStyle{answerthm}{EnvQuestion}{" + BS + "faCommentDots}"])
        for env in ("answer}[theorem]", "question*}", "answer*}"):
            style = "questionthm" if env.startswith("question") else "answerthm"
            insert_before(lines, BS + "newtheorem" + ("*" if "*" in env else "") + "{" + env,
                          [BS + "theoremstyle{" + style + "}"])
    if BS + "setSpeaker" not in body:
        # the speaker of the following speech blocks, shown faintly after the timestamp; unset shows nothing
        title = next(i for i, l in enumerate(lines) if BS + "faMicrophone" in l and "{ [#1]}}}" in l)
        lines[title] = lines[title].replace("{ [#1]}}}", "{ [#1]}" + BS + "speakertag}}", 1)
        insert_before(lines, BS + "newtcolorbox{speech}", SPEAKER.strip("\n").split("\n") + [""])
    if BS + "youtubechapter" not in body:
        insert_before(lines, BS + "titlespacing*{" + BS + "chapter}", YOUTUBE_CHAPTER.strip("\n").split("\n") + [""])

    rel = os.path.relpath(source, LATEX)
    header = [
        "% " + "=" * 70,
        f"% GENERATED by tools/build-language-preambles.py from",
        f"%   {rel}",
        "% Do not edit by hand: edit the book preamble and rerun the script.",
        "% " + "=" * 70,
        "",
    ]
    out = os.path.join(ROOT, f"pdf-preamble-{lang}.tex")
    with open(out, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(header + lines) + "\n")
    print(f"{out}: {len(lines)} lines from {rel}")


for lang, (source, obs, obs_plural, remark) in SOURCES.items():
    build(lang, source, obs, obs_plural, remark)
