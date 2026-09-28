#!/usr/bin/env python3
"""Build and open Marvin with a pinned status row and dim process output."""
from collections import deque
import codecs
import os
from pathlib import Path
import re
import selectors
import shutil
import signal
import subprocess
import sys
import time

DIRECTORY = Path(__file__).resolve().parents[1] / 'apps/simulator-macos'
ANSI = re.compile(r'\x1b\[[0-?]*[ -/]*[@-~]')


class Display:
    def __init__(self):
        self.terminal = sys.stdout.isatty() and os.environ.get('TERM') != 'dumb'
        self.status = 'Preparing Marvin Simulator'
        self.lines = deque(maxlen=200)
        self.partial = ''
        self.started = time.monotonic()
        self.rendered_rows = 0
        self.terminal_size = None

    def line(self, text):
        if text.startswith('\x1e'):
            self.status = text[1:]
            if not self.terminal:
                print(self.status, flush=True)
        elif self.terminal:
            self.lines.append(text)
        else:
            print(text, flush=True)

    def feed(self, text):
        self.partial += text
        while '\n' in self.partial:
            line, self.partial = self.partial.split('\n', 1)
            self.line(line.rstrip('\r'))

    def render(self, finished=False):
        if not self.terminal:
            return
        width, height = shutil.get_terminal_size()
        size = (width, height)
        if self.terminal_size is not None and self.terminal_size != size:
            # Resizing can reflow earlier output. Start a fresh block instead
            # of moving up into rows whose positions are no longer known.
            sys.stdout.write('\r\n')
            self.rendered_rows = 0
        self.terminal_size = size
        width = max(1, width - 1)
        elapsed = time.monotonic() - self.started
        spinner = '⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏'[int(elapsed * 10) % 10]
        header = f'{"•" if finished else spinner} {self.status}  ({elapsed:.1f}s)'
        lines = list(self.lines)
        if self.partial and not self.partial.startswith('\x1e'):
            lines.append(self.partial)
        # Strip child terminal controls so they cannot overwrite the status row.
        rows = []
        for line in lines:
            clean = ''.join(c for c in ANSI.sub('', line).expandtabs(4) if c.isprintable())
            rows.extend(clean[i:i + width] for i in range(0, max(1, len(clean)), width))
        rows = rows[-max(1, height - 2):]
        # Redraw only the block we printed, preserving the shell above it.
        # Keep its footprint when output shrinks so stale lines are erased.
        count = max(self.rendered_rows, 1 + len(rows))
        rows.extend([''] * (count - 1 - len(rows)))
        sys.stdout.write('\r')
        if self.rendered_rows > 1:
            sys.stdout.write(f'\x1b[{self.rendered_rows - 1}A')
        sys.stdout.write('\x1b[2K\x1b[0m' + header[:width])
        for row in rows:
            sys.stdout.write('\r\n\x1b[2K\x1b[90m' + row)
        sys.stdout.write('\x1b[0m')
        self.rendered_rows = count
        sys.stdout.flush()


def run(command, display):
    env = dict(os.environ, MARVIN_PROGRESS='1', PYTHONUNBUFFERED='1', TERM='dumb', NO_COLOR='1')
    process = subprocess.Popen(command, cwd=DIRECTORY, env=env,
                               stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                               start_new_session=True)
    decoder = codecs.getincrementaldecoder('utf-8')(errors='replace')
    try:
        with selectors.DefaultSelector() as selector:
            selector.register(process.stdout, selectors.EVENT_READ)
            while selector.get_map():
                for key, _ in selector.select(timeout=0.1):
                    chunk = os.read(key.fileobj.fileno(), 65536)
                    if chunk:
                        display.feed(decoder.decode(chunk))
                    else:
                        selector.unregister(key.fileobj)
                        display.feed(decoder.decode(b'', final=True))
                display.render()
        if display.partial:
            display.line(display.partial)
            display.partial = ''
        return process.wait()
    finally:
        if process.poll() is None:
            os.killpg(process.pid, signal.SIGTERM)
            try:
                process.wait(timeout=2)
            except subprocess.TimeoutExpired:
                os.killpg(process.pid, signal.SIGKILL)
                process.wait()
        process.stdout.close()


def main():
    display = Display()
    if display.terminal:
        sys.stdout.write('\x1b[?25l')
    try:
        display.render()
        result = run(['./build-app.sh'], display)
        if result:
            display.status = f'Build failed (exit {result})'
        else:
            display.status = 'Opening Marvin Simulator'
            display.render()
            result = run(['open', '.build/Marvin Simulator.app'], display)
            display.status = 'Marvin Simulator launched' if result == 0 else f'Launch failed (exit {result})'
    except KeyboardInterrupt:
        display.status = 'Launch cancelled'
        result = 130
    except OSError as error:
        display.line(str(error))
        display.status = 'Launch failed'
        result = 1
    finally:
        display.render(finished=True)
        if display.terminal:
            sys.stdout.write('\x1b[0m\x1b[?25h\n')
            sys.stdout.flush()
        else:
            print(display.status, flush=True)
    return result


if __name__ == '__main__':
    sys.exit(main())
