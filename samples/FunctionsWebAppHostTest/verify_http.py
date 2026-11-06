"""Verify the sample locally or in Azure using only Python's standard library."""

import argparse
import concurrent.futures
import datetime
import hashlib
import http.client
import json
import time
import urllib.error
import urllib.parse
import urllib.request


def request(base_url, path, status=200, method="GET", body=None):
    headers = {"Content-Type": "application/json"} if body is not None else {}
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(base_url + path, data=data, headers=headers, method=method)
    try:
        response = urllib.request.urlopen(req, timeout=10)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        assert response.status == status, (path, response.status, response.read())
        return response.headers, response.read()


def verify_responses(base_url):
    request(base_url, "/")
    _, head = request(base_url, "/", status=405, method="HEAD")
    assert head == b""
    for kind in ("controller", "minimal"):
        headers, body = request(base_url, f"/{kind}/42?message=hello%20world")
        value = json.loads(body)
        assert value["id"] == 42 and value["message"] == "hello world", value
        assert value["scopeId"] == headers["X-Application-Scope"]
        _, body = request(base_url, f"/{kind}/echo", method="POST", body={"message": kind})
        assert json.loads(body) == {"message": kind}
        request(base_url, f"/{kind}/echo", method="POST", body="not-an-object", status=400)
    headers, _ = request(base_url, "/response", status=202)
    assert headers["X-Adapter-Probe"] == "passed"
    cookies = headers.get_all("Set-Cookie")
    assert len(cookies) == 2 and cookies[0].startswith("adapter=one") and cookies[1].startswith("second=two")
    _, binary = request(base_url, "/binary")
    assert binary == bytes([0, 1, 127, 128, 255])
    request(base_url, "/missing", status=404)
    request(base_url, "/controller/42", status=405, method="DELETE")
    _, body = request(base_url, "/error", status=500)
    assert json.loads(body)["status"] == 500
    assert b"Intentional adapter probe failure" not in body
    with concurrent.futures.ThreadPoolExecutor(max_workers=8) as executor:
        responses = list(executor.map(lambda index: request(base_url, f"/minimal/{index}"), range(20)))
    scopes = []
    for index, (headers, body) in enumerate(responses):
        value = json.loads(body)
        assert value["id"] == index
        assert value["scopeId"] == headers["X-Application-Scope"]
        scopes.append(value["scopeId"])
    assert len(set(scopes)) == 20
    print("PASS controllers, minimal APIs, bindings, status/headers/cookies, binary, errors, 20 concurrent scopes")


def verify_response_stream(base_url):
    start = time.monotonic()
    arrivals = []
    with urllib.request.urlopen(base_url + "/stream", timeout=10) as response:
        for expected in (b"chunk-1\n", b"chunk-2\n", b"chunk-3\n"):
            assert response.readline() == expected
            arrivals.append(time.monotonic() - start)
        assert response.read() == b""
    assert arrivals[0] < arrivals[-1] - 0.5, arrivals
    assert all(later - earlier >= 0.5 for earlier, later in zip(arrivals, arrivals[1:])), arrivals
    print("PASS incremental response arrivals:", ", ".join(f"{value:.3f}s" for value in arrivals))


def probe_upload(base_url):
    chunk_bytes = 65536
    target = urllib.parse.urlsplit(base_url)
    connection_type = http.client.HTTPSConnection if target.scheme == "https" else http.client.HTTPConnection
    connection = connection_type(target.hostname, target.port, timeout=60)
    connection.putrequest("POST", target.path.rstrip("/") + "/stream/upload")
    connection.putheader("Transfer-Encoding", "chunked")
    connection.putheader("Content-Type", "application/octet-stream")
    connection.endheaders()
    print("UPLOAD TEST: 2 chunks of 64 KiB, one second apart")
    chunks = [bytes([ord("a") + index]) * (chunk_bytes - 1) + b"\n" for index in range(2)]
    try:
        for index, chunk in enumerate(chunks, 1):
            if index > 1:
                time.sleep(1)
            connection.send(f"{len(chunk):x}\r\n".encode() + chunk + b"\r\n")
            print(f"Upload chunk {index}/{len(chunks)} sent at", datetime.datetime.now(datetime.timezone.utc).isoformat())
        print("Upload EOF sent at", datetime.datetime.now(datetime.timezone.utc).isoformat())
        connection.send(b"0\r\n\r\n")
        with connection.getresponse() as response:
            assert response.status == 200, (response.status, response.read())
            result = json.load(response)
        assert result["bytesReceived"] == len(chunks) * chunk_bytes, result
        assert result["sha256"] == hashlib.sha256(b"".join(chunks)).hexdigest(), result
    finally:
        connection.close()
    print(f"PASS upload file byte count and SHA-256: {len(chunks) * chunk_bytes} bytes")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-url", default="http://localhost:7099")
    parser.add_argument("--upload-only", action="store_true", help="Run only the upload integrity test")
    options = parser.parse_args()
    if not options.upload_only:
        verify_responses(options.base_url.rstrip("/"))
        verify_response_stream(options.base_url.rstrip("/"))
    probe_upload(options.base_url.rstrip("/"))
