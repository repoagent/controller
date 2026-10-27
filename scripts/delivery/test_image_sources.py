"""The controller image's source inventory and the build of the context composed from it.

The image is built only from the files `image/source-files.txt` lists. These tests check that the inventory covers
every evaluated input of the shipped controller project and of each project it references, such as the controller
core, and that the default composed context builds with warnings as errors.
"""
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
# Evaluated items the compiler or the published output reads. Each project file is an input too.
INPUT_ITEMS = ('Compile', 'Content', 'EmbeddedResource', 'AdditionalFiles')


def controller_inputs():
    """Checkout-relative inputs of the controller project and every project it references, transitively."""
    inputs, pending = set(), [ROOT / 'src/RepoAgentController/RepoAgentController.csproj']
    while pending:
        project = pending.pop()
        if project in inputs:
            continue
        inputs.add(project)
        result = subprocess.run(['dotnet', 'msbuild', str(project), '-getItem:' + ','.join(INPUT_ITEMS + ('ProjectReference',))],
                                text=True, capture_output=True, check=True, timeout=60)
        items = json.loads(result.stdout)['Items']
        inputs.update(Path(item['FullPath']) for kind in INPUT_ITEMS for item in items.get(kind, []))
        pending += [Path(item['FullPath']) for item in items.get('ProjectReference', [])]
    return {str(path.relative_to(ROOT)) for path in inputs}


class ControllerImageSources(unittest.TestCase):
    def test_default_image_contains_every_evaluated_input_of_the_controller_and_its_core(self):
        expected = controller_inputs() | {'global.json', 'Directory.Build.props', '.editorconfig'}
        self.assertIn('src/RepoAgentController.Core/RepoAgentController.Core.csproj', expected)
        inventory = {line.strip() for line in (ROOT / 'image/source-files.txt').read_text().splitlines()
                     if line.strip() and not line.startswith('#')}
        self.assertFalse(expected - inventory, 'Shipping context omits controller inputs: ' + repr(sorted(expected - inventory)))

    def test_actual_default_composed_context_builds_without_injected_sources(self):
        with tempfile.TemporaryDirectory(prefix='checkout shipping ') as directory:
            context = Path(directory) / 'context'
            composition = subprocess.run([sys.executable, str(ROOT / 'scripts/compose-image.py'), '--source', str(ROOT),
                                         '--manifest', str(ROOT / 'image/default.json'), '--output', str(context)],
                                        capture_output=True, text=True, timeout=60)
            self.assertEqual(composition.returncode, 0, composition.stdout + composition.stderr)
            config = Path(directory) / 'nuget.config'
            config.write_text('<configuration><packageSources><clear/><add key="public" value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>')
            environment = dict(os.environ, DOTNET_CLI_HOME=str(Path(directory) / 'cli-home'),
                               DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1', DOTNET_CLI_TELEMETRY_OPTOUT='1')
            result = subprocess.run(['dotnet', 'build', str(context / 'src/RepoAgentController'), '-c', 'Release',
                                     '--nologo', '--configfile', str(config), '-p:TreatWarningsAsErrors=true', '-p:NuGetAudit=false'],
                                    env=environment, capture_output=True, text=True, timeout=120)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            # The shipping context must enforce the checkout's policy, not just compile its sources.
            (context / 'src/RepoAgentController/CancellationPolicyProbe.cs').write_text(
                'internal static class CancellationPolicyProbe { '
                'internal static System.Threading.Tasks.Task Run(System.Threading.CancellationToken token) '
                '=> System.Threading.Tasks.Task.Delay(1); }')
            rejected = subprocess.run(['dotnet', 'build', str(context / 'src/RepoAgentController'), '-c', 'Release',
                                       '--no-restore', '--nologo'], env=environment, capture_output=True,
                                      text=True, timeout=120)
            self.assertNotEqual(rejected.returncode, 0, 'Shipping context accepted a cancellation policy violation')
            self.assertIn('error CA2016', rejected.stdout + rejected.stderr)


if __name__ == '__main__':
    unittest.main()
