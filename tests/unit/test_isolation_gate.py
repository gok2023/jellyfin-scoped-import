"""Offline safety tests: no server, credentials or media fixtures are needed."""
import importlib.util
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

SOURCE = Path(__file__).resolve().parents[1] / 'integration/support/lab.py'
spec = importlib.util.spec_from_file_location('scopedimport_lab', SOURCE)
lab = importlib.util.module_from_spec(spec)
spec.loader.exec_module(lab)

class IsolationGateTests(unittest.TestCase):
    def setUp(self):
        lab._VERIFIED = False
        lab.TOKEN = None

    def test_default_connect_has_no_http_or_process_side_effects(self):
        with patch.dict(os.environ, {}, clear=True), patch.object(lab.OPENER, 'open') as http, patch.object(lab.subprocess, 'run') as process:
            with self.assertRaises(lab.IsolationError):
                lab.connect()
            http.assert_not_called()
            process.assert_not_called()

    def test_api_cannot_bypass_connection_verification(self):
        with patch.object(lab.OPENER, 'open') as http:
            with self.assertRaises(lab.IsolationError):
                lab.api('POST', '/ScopedImport/Import', {'Path': 'irrelevant'})
            http.assert_not_called()

    def test_wrong_directory_rejected_before_http(self):
        with patch.dict(os.environ, {'SCOPEDIMPORT_INTEGRATION': 'DISPOSABLE-HOST-ONLY'}), patch.object(lab, 'LAB', Path(tempfile.gettempdir())), patch.object(lab.OPENER, 'open') as http:
            with self.assertRaises(lab.IsolationError):
                lab.connect()
            http.assert_not_called()

    def test_production_and_non_loopback_addresses_rejected(self):
        for endpoint in ['http://127.0.0.1:8096', 'http://192.0.2.1:18096', 'http://localhost:18096', 'https://127.0.0.1:18096']:
            with self.subTest(endpoint=endpoint), patch.dict(os.environ, {'SCOPEDIMPORT_INTEGRATION': 'DISPOSABLE-HOST-ONLY'}), patch.object(lab, 'BASE', endpoint), patch.object(lab.OPENER, 'open') as http:
                with self.assertRaises(lab.IsolationError):
                    lab.connect()
                http.assert_not_called()

    def test_fake_marker_cannot_enable_network(self):
        with patch.dict(os.environ, {'SCOPEDIMPORT_INTEGRATION': 'DISPOSABLE-HOST-ONLY'}), patch.object(Path, 'read_text', return_value='{"kind":"other","Password":"sensitive-fixture-sentinel"}'), patch.object(lab.OPENER, 'open') as http:
            with self.assertRaises(lab.IsolationError) as caught:
                lab.connect()
            self.assertNotIn('sensitive-fixture-sentinel', str(caught.exception))
            http.assert_not_called()

    def test_process_failure_suppresses_command_and_credentials(self):
        marker = {'pid': 123}
        with patch.object(lab.os, 'name', 'nt'), patch.object(lab.subprocess, 'run', side_effect=RuntimeError('sensitive-fixture-sentinel')):
            with self.assertRaises(lab.IsolationError) as caught:
                lab._process_gate(marker)
            self.assertNotIn('sensitive-fixture-sentinel', str(caught.exception))

    def test_root_reparse_point_is_refused_without_http(self):
        original_lstat = lab.os.lstat
        def fake_lstat(path, *args, **kwargs):
            if Path(path) == lab.LAB:
                from types import SimpleNamespace
                return SimpleNamespace(st_file_attributes=lab.stat.FILE_ATTRIBUTE_REPARSE_POINT)
            return original_lstat(path, *args, **kwargs)
        with patch.dict(os.environ, {'SCOPEDIMPORT_INTEGRATION': 'DISPOSABLE-HOST-ONLY'}), patch.object(lab.os, 'lstat', side_effect=fake_lstat), patch.object(lab.OPENER, 'open') as http:
            with self.assertRaises(lab.IsolationError):
                lab.connect()
            http.assert_not_called()
    def test_identity_mismatch_never_sends_credentials(self):
        marker = {'server_id': '0' * 32, 'pid': 123}
        with patch.object(lab, '_local_gate', return_value=marker), patch.object(lab, '_process_gate'), patch.object(lab, '_request', return_value=(200, {'Id': 'wrong'})) as request, patch.object(Path, 'read_text') as read:
            with self.assertRaises(lab.IsolationError):
                lab.connect()
            request.assert_called_once_with('GET', '/System/Info/Public')
            read.assert_not_called()
            self.assertIsNone(lab.TOKEN)

    def test_marker_without_process_evidence_cannot_enable_http(self):
        marker = {'server_id': '0' * 32, 'pid': 123}
        with patch.object(lab, '_local_gate', return_value=marker), patch.object(lab, '_process_gate', side_effect=lab.IsolationError('Unverified process')), patch.object(lab.OPENER, 'open') as http:
            with self.assertRaises(lab.IsolationError):
                lab.connect()
            http.assert_not_called()
    def test_path_escape_and_fixture_write_before_connect_fail(self):
        with self.assertRaises(lab.IsolationError):
            lab._inside(lab.LAB / 'media/../../../../outside', lab.LAB / 'media')
        with patch.object(lab.subprocess, 'run') as process:
            with self.assertRaises(lab.IsolationError):
                lab.video('other/fake.mp4')
            process.assert_not_called()

    def test_unsafe_routes_are_refused_without_network(self):
        for route in ['/Library/Refresh', '/System/Shutdown', '/System/Restart', '/LabAppendProbe/Prepare', '/Startup/User', '//remote/Import']:
            with self.subTest(route=route), patch.object(lab.OPENER, 'open') as http:
                with self.assertRaises(lab.IsolationError):
                    lab._request('POST', route)
                http.assert_not_called()

if __name__ == '__main__':
    unittest.main()
