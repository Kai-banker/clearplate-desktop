#!/usr/bin/env python3
"""Exercise the shipped local capture TLS listener without OS trust/proxy or upstream calls."""
import base64, hashlib, json, pathlib, socket, ssl, subprocess, sys
helper = pathlib.Path(sys.argv[1]).resolve()
def settings():
    values = []
    for command in [['/usr/sbin/scutil','--proxy'], ['/usr/bin/security','dump-trust-settings']]:
        result = subprocess.run(command, capture_output=True, check=False)
        values.append(hashlib.sha256(result.stdout + result.stderr).digest())
    return values
before = settings()
process = subprocess.Popen([str(helper),'--stdio-capture'], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
try:
    ready = json.loads(process.stdout.readline())
    assert ready['type'] == 'ready' and 1024 <= ready['port'] <= 65535
    pem = ssl.DER_cert_to_PEM_cert(base64.b64decode(ready['certificate']))
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
    context.load_verify_locations(cadata=pem)
    def tunnel():
        sock = socket.create_connection(('127.0.0.1',ready['port']),timeout=10)
        sock.sendall(b'CONNECT clearplate-api.guangpanxingdong.com:443 HTTP/1.1\r\nHost: clearplate-api.guangpanxingdong.com:443\r\n\r\n')
        head = b''
        while not head.endswith(b'\r\n\r\n'): head += sock.recv(1)
        assert head.startswith(b'HTTP/1.1 200')
        return sock
    with context.wrap_socket(tunnel(),server_hostname='clearplate-api.guangpanxingdong.com') as tls:
        assert tls.version() in ('TLSv1.2','TLSv1.3')
        assert ('DNS','clearplate-api.guangpanxingdong.com') in tls.getpeercert()['subjectAltName']
        # Do not send an HTTP request: there is no platform upstream request.
    sock = tunnel()
    try:
        context.wrap_socket(sock,server_hostname='unrelated.example')
        raise AssertionError('wrong hostname was accepted')
    except ssl.SSLCertVerificationError: pass
    finally: sock.close()
    process.stdin.write(b'stop\n'); process.stdin.flush()
    assert process.wait(timeout=15) == 0
finally:
    if process.poll() is None: process.kill(); process.wait()
assert settings() == before, 'system proxy or user trust changed during isolated test'
print('Mac capture TLS checks passed: 4 (valid scoped TLS, wrong host rejected, clean exit, unchanged OS proxy/trust). No upstream request sent.')
