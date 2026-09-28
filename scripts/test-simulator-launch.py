#!/usr/bin/env python3
"""Focused checks: python3 scripts/test-simulator-launch.py."""
import contextlib
import importlib.util
import io
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


def load(name):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(name + '.py'))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


assets = load('prepare-simulator-assets')
launcher = load('launch-marvin')


class AssetCacheTests(unittest.TestCase):
    def test_changes_missing_outputs_and_failed_export(self):
        with tempfile.TemporaryDirectory() as directory, contextlib.redirect_stdout(io.StringIO()):
            root = Path(directory)
            old_root, old_cache = assets.ROOT, assets.CACHE
            self.addCleanup(setattr, assets, 'ROOT', old_root)
            self.addCleanup(setattr, assets, 'CACHE', old_cache)
            assets.ROOT, assets.CACHE = root, root / '.cache'
            (root / 'scripts').mkdir()
            (root / 'scripts/prepare-simulator-assets.py').write_text('cache-v1')
            source = root / 'source'
            source.write_text('one')
            exporter = root / 'scripts/export.py'
            output = root / assets.RESOURCES / 'Fake/Generated'
            exporter.write_text(
                'from pathlib import Path\n'
                f'out = Path({str(output)!r})\n'
                'out.mkdir(parents=True, exist_ok=True)\n'
                f'(out / "mesh").write_text(Path({str(source)!r}).read_text())\n')
            job = ('Fake', 'export.py', ['source'], ['Fake/Generated'])
            assets.prepare(*job)
            mesh = output / 'mesh'
            # A cache hit must leave outputs untouched, even with old mtimes.
            import os
            os.utime(mesh, ns=(1_000_000_000, 1_000_000_000))
            assets.prepare(*job)
            self.assertEqual(mesh.stat().st_mtime_ns, 1_000_000_000)
            source.write_text('two')
            assets.prepare(*job)
            self.assertEqual(mesh.read_text(), 'two')
            mesh.unlink()
            assets.prepare(*job)
            self.assertTrue(mesh.exists())
            mesh.write_text('damaged')
            assets.prepare(*job)
            self.assertEqual(mesh.read_text(), 'two')
            exporter.write_text('raise SystemExit(7)\n')
            with self.assertRaises(subprocess.CalledProcessError):
                assets.prepare(*job)
            self.assertFalse((assets.CACHE / 'export.py.json').exists())


class ProgressTests(unittest.TestCase):
    def test_stage_output_and_failure_exit(self):
        with contextlib.redirect_stdout(io.StringIO()) as output:
            display = launcher.Display()
            result = launcher.run([sys.executable, '-c',
                'import sys; print("\\x1eCompiling"); print("diagnostic", file=sys.stderr); '
                'print("partial", end=""); sys.exit(7)'], display)
        self.assertEqual(result, 7)
        self.assertEqual(display.status, 'Compiling')
        self.assertIn('diagnostic', output.getvalue())
        self.assertIn('partial', output.getvalue())
        self.assertNotIn('\x1b', output.getvalue())
        self.assertNotIn('\x1e', output.getvalue())

    def test_pinned_header_and_grey_output(self):
        with contextlib.redirect_stdout(io.StringIO()) as output:
            display = launcher.Display()
            display.terminal = True
            display.feed('\x1eSigning\nchild output\n')
            display.render()
            display.render()
        self.assertIn('\x1b[1A', output.getvalue())
        self.assertNotIn('\x1b[H', output.getvalue())
        self.assertNotIn('\x1b[J', output.getvalue())
        self.assertNotIn('\x1b[2J', output.getvalue())
        self.assertIn('Signing', output.getvalue().split('\n')[0])
        self.assertIn('\x1b[90m', output.getvalue())
        self.assertIn('child output', output.getvalue())


if __name__ == '__main__':
    unittest.main()
