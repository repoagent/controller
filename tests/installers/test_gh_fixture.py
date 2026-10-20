"""External GitHub fixture wire bytes are independent of stdout newline policy."""
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


class GitHubFixtureWireTests(unittest.TestCase):
    def test_model_response_uses_canonical_http_bytes_under_windows_stdout(self):
        fixture = Path(__file__).with_name('gh_fixture.py').resolve()
        expected = (b'HTTP/2.0 200 Fixture\r\nContent-Type: application/json\r\n\r\n'
                    b'{"id": 123, "full_name": "example/project"}\n')
        for newline in ('\n', '\r\n'):
            with self.subTest(stdout_newline=repr(newline)), tempfile.TemporaryDirectory() as temporary:
                directory = Path(temporary)
                model = directory / 'model.json'
                model.write_text(json.dumps({'variables': {}}), encoding='utf-8')
                # Run the actual external fixture with Windows text translation;
                # binary writes must retain exact HTTP framing on either platform.
                program = 'import runpy,sys;sys.stdout.reconfigure(newline=sys.argv.pop(1));runpy.run_path(sys.argv.pop(1),run_name="__main__")'
                result = subprocess.run([sys.executable, '-c', program, newline, str(fixture),
                    'api', '--hostname', 'github.com', '--method', 'GET', '--include', 'repos/example/project'],
                    env=dict(os.environ, HOST_ENROLLMENT_GH_MODEL=str(model), REVOKE_GH_LOG=str(directory / 'calls.jsonl')),
                    capture_output=True, timeout=10)
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertEqual(result.stdout, expected)
