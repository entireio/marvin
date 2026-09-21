"""Exercise the real backup wrapper with a fake esptool; no serial device access."""
import os
from pathlib import Path
import shutil
import signal
import subprocess
import sys
import tempfile
import time
import unittest

class BackupProgressTest(unittest.TestCase):
    def test_backspace_progress_reaches_log_before_command_finishes(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            script = root / 'firmware' / 'tools' / 'board-backup.py'
            script.parent.mkdir(parents=True)
            shutil.copyfile(Path(__file__).resolve().parents[2] / 'firmware/tools/board-backup.py', script)
            (root / 'esptool.py').write_text('''import sys,time
if 'read_flash' in sys.argv:
    sys.stdout.write('4096 (1 %)'+chr(8)*10)
    sys.stdout.flush()
    time.sleep(30)
else:
    print('ESP32-S3; Detected flash size: 16MB')
''')
            with (root / 'terminal.txt').open('wb') as terminal:
                child = subprocess.Popen([sys.executable, str(script)], stdout=terminal, stderr=terminal,
                                         env={**os.environ, 'PYTHONPATH':str(root)})
                try:
                    deadline = time.monotonic()+5
                    while time.monotonic()<deadline:
                        logs=list((root/'work/board').glob('backup-*/read.txt'))
                        if logs and b'4096 (1 %)' in logs[0].read_bytes():
                            break
                        time.sleep(.02)
                    else:
                        self.fail('Progress was buffered until a newline or process exit')
                    self.assertIsNone(child.poll(), 'Read command must still be running')
                    self.assertIn(b'4096 (1 %)', (root/'terminal.txt').read_bytes())
                finally:
                    child.send_signal(signal.SIGINT)
                    child.wait(timeout=5)
                self.assertEqual(child.returncode,130)

if __name__=='__main__':
    unittest.main()
