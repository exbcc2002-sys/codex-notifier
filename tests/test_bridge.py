"""Validate the shipped raw-string Python bridge without reading user configs."""
import ast
import base64
import json
from pathlib import Path
import sys
import tempfile
import types
import unittest
from unittest.mock import patch

SOURCE = (Path(__file__).resolve().parents[1] / 'src/CodexNotifier.Core/Integration.cs').read_text()
BRIDGE = SOURCE.split('public const string PythonBridge = """', 1)[1].split('""";', 1)[0]

class BridgeTests(unittest.TestCase):
    def test_syntax(self):
        ast.parse(BRIDGE)

    def test_metadata_and_original_callback(self):
        calls = []
        fake = types.ModuleType('subprocess')
        fake.DEVNULL = -3
        fake.TimeoutExpired = TimeoutError
        fake.Popen = lambda args, **kwargs: calls.append(('previous', args))
        fake.run = lambda args, **kwargs: calls.append(('relay', args))
        with tempfile.TemporaryDirectory(prefix='cn-bridge-test-') as tmp:
            path = Path(tmp) / 'bridge.py'
            path.with_name('integration.json').write_text(json.dumps({
                'SourceId': 's', 'WindowsRelay': r'C:\Users\测试 用户\relay.exe',
                'ForwardPrevious': True, 'PreviousNotify': ['old', 'arg with spaces']}))
            namespace = {'__file__': str(path), '__name__': 'bridge_test'}
            payload = json.dumps({'type': 'agent-turn-complete', 'thread-id': 't',
                                  'turn-id': 'u', 'last-assistant-message': 'PRIVATE_SENTINEL'})
            with patch.dict(sys.modules, {'subprocess': fake}), patch.object(sys, 'argv', ['bridge.py', '--notify', payload]):
                exec(compile(BRIDGE, str(path), 'exec'), namespace)
                namespace['main']()
        self.assertEqual(calls[0][1][:2], ['old', 'arg with spaces'])
        self.assertEqual(calls[1][1][0], '/mnt/c/Users/测试 用户/relay.exe')
        event = base64.b64decode(calls[1][1][2]).decode()
        self.assertNotIn('PRIVATE_SENTINEL', event)
        self.assertEqual(json.loads(event)['TurnId'], 'u')

if __name__ == '__main__':
    unittest.main()
