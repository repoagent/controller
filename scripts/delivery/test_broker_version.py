"""Broker packaging diagnostics through the actual built executable, without provider access."""
import json
from pathlib import Path
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
PROJECT = ROOT / 'service/auth/src/RepoAgent.Auth/RepoAgent.Auth.csproj'
SOURCE = 'b' * 40


class BrokerVersionDiagnostics(unittest.TestCase):
    def test_compiled_version_and_source_are_available_without_broker_configuration(self):
        with tempfile.TemporaryDirectory() as directory:
            result = subprocess.run(['dotnet', 'build', str(PROJECT), '--configuration', 'Release',
                                     '--output', directory, '--nologo', '-p:Version=2.4.6',
                                     '-p:SourceRevisionId=' + SOURCE], cwd=ROOT, capture_output=True,
                                    text=True, timeout=180)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            diagnostic = subprocess.run(['dotnet', str(Path(directory) / 'RepoAgent.Auth.dll'),
                                         '--version', '--json'], capture_output=True, text=True, timeout=15)
            self.assertEqual(diagnostic.returncode, 0, diagnostic.stdout + diagnostic.stderr)
            self.assertEqual(json.loads(diagnostic.stdout), {
                'component': 'broker', 'version': '2.4.6', 'source': SOURCE})


if __name__ == '__main__':
    unittest.main()
