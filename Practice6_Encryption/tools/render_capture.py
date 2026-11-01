"""Рисует вывод tshark (docs/evidence/wireshark_data_*.txt) как SVG «окна» для Security.md.

    python tools/render_capture.py

Это вывод консольного Wireshark (tshark) по настоящим pcap из docs/captures, а не скриншот GUI.
Строки с паролем подсвечиваются.
"""
import html
import os

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
EV = os.path.join(ROOT, "docs", "evidence")
OUT = os.path.join(ROOT, "docs", "screenshots")


def render(src, dst, title, highlight):
    with open(src, encoding="utf-8") as f:
        lines = [l.rstrip("\n") for l in f]
    # длинную строку Data переносим по 64 символа
    wrapped = []
    for l in lines:
        while len(l) > 96:
            wrapped.append(l[:96])
            l = "          " + l[96:]
        wrapped.append(l)
    w, lh, top = 860, 17, 44
    h = top + lh * len(wrapped) + 16
    out = [f'<svg xmlns="http://www.w3.org/2000/svg" width="{w}" height="{h}" font-family="Consolas, DejaVu Sans Mono, monospace" font-size="13">',
           f'<rect width="{w}" height="{h}" rx="8" fill="#1e1f24"/>',
           f'<rect width="{w}" height="30" rx="8" fill="#2d2f36"/><rect y="22" width="{w}" height="8" fill="#2d2f36"/>',
           '<circle cx="18" cy="15" r="5" fill="#ff5f57"/><circle cx="36" cy="15" r="5" fill="#febc2e"/><circle cx="54" cy="15" r="5" fill="#28c840"/>',
           f'<text x="{w / 2}" y="20" fill="#c9ccd3" text-anchor="middle" font-family="Segoe UI, Arial" font-size="13">{html.escape(title)}</text>']
    for i, l in enumerate(wrapped):
        y = top + i * lh
        if any(k in l for k in highlight):
            out.append(f'<rect x="8" y="{y - 12}" width="{w - 16}" height="{lh}" fill="#5a2a2a"/>')
        color = "#7ec7ff" if l.startswith("$") else ("#ffd479" if l.startswith(("Data", "User Datagram")) else "#d7dae0")
        out.append(f'<text x="14" y="{y}" fill="{color}" xml:space="preserve" style="white-space:pre">{html.escape(l)}</text>')
    out.append("</svg>")
    with open(dst, "w", encoding="utf-8") as f:
        f.write("\n".join(out))


def main():
    os.makedirs(OUT, exist_ok=True)
    render(os.path.join(EV, "wireshark_data_before_plain.txt"), os.path.join(OUT, "capture_before.svg"),
           "До шифрования: LOGIN в открытом виде (tshark, before_plain.pcap)", ["ima....", "3cr3t-pass", "7333637233742d70617373"])
    render(os.path.join(EV, "wireshark_data_after_encrypted.txt"), os.path.join(OUT, "capture_after.svg"),
           "После шифрования: тот же LOGIN внутри ENCRYPTED (tshark, after_encrypted.pcap)", [])


if __name__ == "__main__":
    main()
