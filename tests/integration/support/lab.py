"""Fail-closed support for manually provisioned, disposable integration hosts.
Importing this module performs no I/O. It never starts, stops or scans a host.
"""
import hashlib
import json
import os
import re
import stat
import subprocess
import urllib.error
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET
from pathlib import Path

PROJECT = Path(__file__).resolve().parents[3]
LAB = PROJECT / '.local' / 'integration-host'
ROOT = LAB
BASE = 'http://127.0.0.1:18096'
TOKEN = None
_VERIFIED = False
_MARKER = None

class IsolationError(RuntimeError):
    pass

class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        raise IsolationError('HTTP redirects are forbidden')

OPENER = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())

def _inside(path, root=LAB):
    path = Path(path)
    root = Path(root)
    if not path.absolute().is_relative_to(root.absolute()):
        raise IsolationError('Path outside the disposable host')
    for parent in [path, *path.parents]:
        try:
            attributes = getattr(os.lstat(parent), 'st_file_attributes', 0)
        except FileNotFoundError:
            attributes = 0
        if parent.is_symlink() or attributes & stat.FILE_ATTRIBUTE_REPARSE_POINT:
            raise IsolationError('Linked host or fixture path refused')
    if not path.resolve().is_relative_to(root.resolve()):
        raise IsolationError('Resolved path escaped disposable host')
    return path

def _local_gate():
    if os.environ.get('SCOPEDIMPORT_INTEGRATION') != 'DISPOSABLE-HOST-ONLY':
        raise IsolationError('Integration is disabled; explicit opt-in and a verified disposable host are required')
    if BASE != 'http://127.0.0.1:18096':
        raise IsolationError('Only the fixed loopback test endpoint is allowed')
    expected = PROJECT / '.local' / 'integration-host'
    if LAB.absolute() != expected.absolute() or ROOT.absolute() != expected.absolute():
        raise IsolationError('Unexpected integration host directory')
    _inside(LAB)
    try:
        marker = json.loads(_inside(LAB / 'isolation.json').read_text(encoding='utf-8'))
        if marker.get('kind') != 'ScopedImport-disposable-host-v1' or marker.get('version') != '10.11.11':
            raise IsolationError('Invalid disposable host marker')
        if not re.fullmatch(r'[0-9a-f]{32}', marker.get('server_id', '')):
            raise IsolationError('Invalid test server identity')
        if not isinstance(marker.get('pid'), int) or marker['pid'] <= 0:
            raise IsolationError('Invalid test process identity')
        for name in ('server', 'data', 'config', 'cache', 'logs', 'media', 'evidence'):
            if not _inside(LAB / name).is_dir():
                raise IsolationError('Disposable host is not fully provisioned')
        network = ET.parse(_inside(LAB / 'config/network.xml')).getroot()
        if network.findtext('InternalHttpPort') != '18096' or [n.text for n in network.findall('LocalNetworkAddresses/string')] != ['127.0.0.1']:
            raise IsolationError('Unsafe test network configuration')
        if network.findtext('EnableHttps', 'false').lower() != 'false':
            raise IsolationError('HTTPS test listener is not supported')
    except IsolationError:
        raise
    except Exception:
        raise IsolationError('Missing or invalid isolated host prerequisites') from None
    return marker

def _process_gate(marker):
    if os.name != 'nt':
        raise IsolationError('Windows process isolation verification is required')
    script = "$p = Get-CimInstance Win32_Process -Filter 'ProcessId = " + str(marker['pid']) + "'; $listeners = @(Get-NetTCPConnection -State Listen -LocalPort 18096 -ErrorAction Stop); @{ executable=$p.ExecutablePath; command=$p.CommandLine; listeners=@($listeners | Select-Object LocalAddress,OwningProcess) } | ConvertTo-Json -Depth 4 -Compress"
    try:
        result = subprocess.run(['powershell.exe', '-NoProfile', '-NonInteractive', '-Command', script], capture_output=True, text=True, check=True, timeout=10, creationflags=subprocess.CREATE_NO_WINDOW)
        state = json.loads(result.stdout)
        if Path(state['executable']).resolve() != _inside(LAB / 'server/jellyfin.exe').resolve():
            raise IsolationError('Unexpected test process executable')
        tokens = [x.strip('"') for x in re.findall(r'"[^"]*"|\S+', state['command'])]
        expected = [str(LAB / 'server/jellyfin.exe')]
        for flag, name in (('--datadir', 'data'), ('--configdir', 'config'), ('--cachedir', 'cache'), ('--logdir', 'logs')):
            expected += [flag, str(LAB / name)]
        expected += ['--nowebclient', '--nonetchange', '--ffmpeg', str(LAB / 'server/ffmpeg.exe')]
        if [s.lower().replace('/', '\\') for s in tokens] != [s.lower().replace('/', '\\') for s in expected]:
            raise IsolationError('Test process command line does not match isolated directories')
        if not state['listeners'] or any(x['LocalAddress'] != '127.0.0.1' or x['OwningProcess'] != marker['pid'] for x in state['listeners']):
            raise IsolationError('Test listener identity or binding failed')
    except IsolationError:
        raise
    except Exception:
        raise IsolationError('Unable to verify test process and listener') from None

def _request(method, route, body=None, token=None):
    if not route.startswith('/') or route.startswith('//') or any(x in route for x in ('\\', '\r', '\n', '#')):
        raise IsolationError('Invalid API route')
    if route.split('?')[0] in ('/Library/Refresh', '/System/Shutdown', '/System/Restart') or route.startswith(('/LabAppendProbe', '/TargetedImport', '/Startup')):
        raise IsolationError('Lifecycle, baseline scanning and obsolete probe APIs are forbidden')
    headers = {'Content-Type': 'application/json', 'Authorization': 'MediaBrowser Client="ScopedImportTests", Device="DisposableHost", DeviceId="scoped-import-tests", Version="0.2.0"'}
    if token:
        headers['X-Emby-Token'] = token
    request = urllib.request.Request(BASE + route, headers=headers, method=method, data=None if body is None else json.dumps(body).encode())
    try:
        response = OPENER.open(request, timeout=30)
    except urllib.error.HTTPError as error:
        response = error
    except Exception:
        raise IsolationError('Test HTTP request failed (details suppressed)') from None
    with response:
        data = response.read()
        try:
            data = json.loads(data) if data else None
        except ValueError:
            data = None
        return response.status, data

def api(method, route, body=None, token='current'):
    if not _VERIFIED:
        raise IsolationError('connect() must verify the isolated host before any API request')
    marker = _local_gate()
    if marker != _MARKER:
        raise IsolationError('Isolated host marker changed')
    _process_gate(marker)
    return _request(method, route, body, TOKEN if token == 'current' else token)

def require(method, route, body=None, status=200):
    code, result = api(method, route, body)
    if code != status:
        raise IsolationError(f'Test request returned HTTP {code}, expected {status}; response suppressed')
    return result

def connect():
    global TOKEN, _VERIFIED, _MARKER
    TOKEN = None
    _VERIFIED = False
    marker = _local_gate()
    _process_gate(marker)
    code, public = _request('GET', '/System/Info/Public')
    if code != 200 or not isinstance(public, dict) or public.get('Id') != marker['server_id'] or public.get('Version') != '10.11.11' or public.get('ServerName') != 'SCOPEDIMPORT-DISPOSABLE-TEST':
        raise IsolationError('Public test server identity failed; credentials were not sent')
    try:
        credentials = json.loads(_inside(LAB / 'test-credentials.json').read_text(encoding='utf-8'))
        code, login = _request('POST', '/Users/AuthenticateByName', {'Username': credentials['Name'], 'Pw': credentials['Password']})
        if code != 200 or not login['User']['Policy']['IsAdministrator']:
            raise IsolationError('Disposable administrator authentication failed')
        token = login['AccessToken']
        code, system = _request('GET', '/System/Info', token=token)
        if code != 200 or system['Id'] != marker['server_id'] or system['Version'] != '10.11.11' or system['ServerName'] != 'SCOPEDIMPORT-DISPOSABLE-TEST':
            raise IsolationError('Authenticated test identity failed')
        for key, name in (('CachePath', 'cache'), ('LogPath', 'logs'), ('ProgramDataPath', 'data')):
            if Path(system[key]).resolve() != (LAB / name).resolve():
                raise IsolationError('Authenticated test directory verification failed')
        code, libraries = _request('GET', '/Library/VirtualFolders', token=token)
        if code != 200 or {x['Name'] for x in libraries} != {'Lab TV', 'Lab Movies', 'Lab Other'}:
            raise IsolationError('Disposable fixture libraries are incomplete')
        locations = {'Lab TV': 'tv', 'Lab Movies': 'movies', 'Lab Other': 'other'}
        for library in libraries:
            if [Path(x).resolve() for x in library.get('Locations', [])] != [(LAB / 'media' / locations[library['Name']]).resolve()]:
                raise IsolationError('Fixture library paths do not match the baseline layout')
            if not library.get('Locations'):
                raise IsolationError('Fixture library has no local path')
            for path in library['Locations']:
                _inside(path, LAB / 'media')
            options = library['LibraryOptions']
            if any(options.get(key) for key in ('EnableRealtimeMonitor', 'SaveLocalMetadata', 'MetadataSavers', 'EnableChapterImageExtraction', 'EnableTrickplayImageExtraction')):
                raise IsolationError('Fixture library background or metadata write options are unsafe')
        code, tasks = _request('GET', '/ScheduledTasks', token=token)
        if code != 200 or not isinstance(tasks, list) or any(task.get('State') != 'Idle' or task.get('Triggers') for task in tasks):
            raise IsolationError('Scheduled test tasks must be idle with triggers disabled')
        _process_gate(marker)
    except IsolationError:
        raise
    except Exception:
        raise IsolationError('Disposable authentication or configuration verification failed (details suppressed)') from None
    TOKEN = token
    _MARKER = marker
    _VERIFIED = True
    return login['User']['Id']

def video(relative):
    if not _VERIFIED:
        raise IsolationError('Fixture writes require a verified isolated host')
    _process_gate(_local_gate())
    path = _inside(LAB / 'media' / relative, LAB / 'media')
    path.parent.mkdir(parents=True, exist_ok=True)
    if not path.exists():
        subprocess.run([str(_inside(LAB / 'server/ffmpeg.exe')), '-v', 'error', '-f', 'lavfi', '-i', 'color=c=black:s=160x90:d=0.3', '-c:v', 'libx264', '-pix_fmt', 'yuv420p', str(path)], check=True, creationflags=subprocess.CREATE_NO_WINDOW, timeout=30)
    return path

def snapshot():
    result = require('GET', '/Items?Recursive=true&Fields=Path,ProviderIds,Overview,ParentId&Limit=1000')
    if result['TotalRecordCount'] >= 1000:
        raise IsolationError('Fixture snapshot would be truncated')
    return result

def file_hashes():
    if not _VERIFIED:
        raise IsolationError('Fixture traversal requires a verified isolated host')
    hashes = {}
    for directory, subdirs, files in os.walk(_inside(LAB / 'media'), followlinks=False):
        for name in subdirs:
            _inside(Path(directory) / name, LAB / 'media')
        for name in files:
            path = _inside(Path(directory) / name, LAB / 'media')
            hashes[str(path.relative_to(LAB / 'media'))] = hashlib.sha256(path.read_bytes()).hexdigest()
    return hashes
