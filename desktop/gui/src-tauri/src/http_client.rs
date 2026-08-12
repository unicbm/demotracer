/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

use url::Url;

#[cfg(windows)]
fn request_https(
    method: &str,
    url: &str,
    content_type: Option<&str>,
    request_body: &[u8],
    max_response_bytes: usize,
    timeout_ms: i32,
) -> Result<Vec<u8>, String> {
    use std::ffi::c_void;
    use std::ptr::{null, null_mut};
    use windows_sys::Win32::Networking::WinHttp::{
        WinHttpCloseHandle, WinHttpConnect, WinHttpOpen, WinHttpOpenRequest,
        WinHttpQueryDataAvailable, WinHttpQueryHeaders, WinHttpReadData, WinHttpReceiveResponse,
        WinHttpSendRequest, WinHttpSetTimeouts, WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY,
        WINHTTP_FLAG_SECURE, WINHTTP_QUERY_FLAG_NUMBER, WINHTTP_QUERY_STATUS_CODE,
    };

    struct WinHttpHandle(*mut c_void);
    impl Drop for WinHttpHandle {
        fn drop(&mut self) {
            if !self.0.is_null() {
                unsafe { WinHttpCloseHandle(self.0) };
            }
        }
    }

    fn wide(value: &str) -> Vec<u16> {
        value.encode_utf16().chain(std::iter::once(0)).collect()
    }

    let parsed = Url::parse(url).map_err(|error| format!("invalid URL: {error}"))?;
    if parsed.scheme() != "https" || !parsed.username().is_empty() || parsed.password().is_some() {
        return Err("only credential-free HTTPS URLs are accepted".to_string());
    }
    let host = parsed
        .host_str()
        .filter(|host| !host.is_empty())
        .ok_or_else(|| "HTTPS URL has no host".to_string())?;
    let port = parsed
        .port_or_known_default()
        .ok_or_else(|| "HTTPS URL has no port".to_string())?;
    let mut request_path = parsed.path().to_string();
    if request_path.is_empty() {
        request_path.push('/');
    }
    if let Some(query) = parsed.query() {
        request_path.push('?');
        request_path.push_str(query);
    }

    unsafe {
        let agent = wide(concat!("CS2 DemoTracer/", env!("CARGO_PKG_VERSION")));
        let session = WinHttpHandle(WinHttpOpen(
            agent.as_ptr(),
            WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY,
            null(),
            null(),
            0,
        ));
        if session.0.is_null() {
            return Err("WinHTTP session creation failed".to_string());
        }
        WinHttpSetTimeouts(session.0, timeout_ms, timeout_ms, timeout_ms, timeout_ms);

        let host = wide(host);
        let connection = WinHttpHandle(WinHttpConnect(session.0, host.as_ptr(), port, 0));
        if connection.0.is_null() {
            return Err("WinHTTP connection failed".to_string());
        }

        let verb = wide(method);
        let request_path = wide(&request_path);
        let request = WinHttpHandle(WinHttpOpenRequest(
            connection.0,
            verb.as_ptr(),
            request_path.as_ptr(),
            null(),
            null(),
            null(),
            WINHTTP_FLAG_SECURE,
        ));
        let header = content_type.map(|value| wide(&format!("Content-Type: {value}\r\n")));
        let (header_pointer, header_length) = header.as_ref().map_or((null(), 0), |value| {
            (value.as_ptr(), (value.len() - 1) as u32)
        });
        let (body_pointer, body_length) = if request_body.is_empty() {
            (null(), 0)
        } else {
            (request_body.as_ptr().cast(), request_body.len() as u32)
        };
        if request.0.is_null()
            || WinHttpSendRequest(
                request.0,
                header_pointer,
                header_length,
                body_pointer,
                body_length,
                body_length,
                0,
            ) == 0
            || WinHttpReceiveResponse(request.0, null_mut()) == 0
        {
            return Err("HTTPS request failed".to_string());
        }

        let mut status = 0_u32;
        let mut status_bytes = std::mem::size_of::<u32>() as u32;
        if WinHttpQueryHeaders(
            request.0,
            WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
            null(),
            (&mut status as *mut u32).cast(),
            &mut status_bytes,
            null_mut(),
        ) == 0
        {
            return Err("HTTPS response status was unavailable".to_string());
        }
        if !(200..300).contains(&status) {
            return Err(format!("HTTPS server returned status {status}"));
        }

        let mut body = Vec::new();
        loop {
            let mut available = 0_u32;
            if WinHttpQueryDataAvailable(request.0, &mut available) == 0 {
                return Err("HTTPS response read failed".to_string());
            }
            if available == 0 {
                break;
            }
            let available = available as usize;
            if body.len().saturating_add(available) > max_response_bytes {
                return Err(format!("HTTPS response exceeds {max_response_bytes} bytes"));
            }
            let offset = body.len();
            body.resize(offset + available, 0);
            let mut read = 0_u32;
            if WinHttpReadData(
                request.0,
                body[offset..].as_mut_ptr().cast(),
                available as u32,
                &mut read,
            ) == 0
            {
                return Err("HTTPS response read failed".to_string());
            }
            if read == 0 {
                body.truncate(offset);
                break;
            }
            body.truncate(offset + read as usize);
        }
        Ok(body)
    }
}

#[cfg(windows)]
pub(crate) fn get_https(url: &str, max_bytes: usize, timeout_ms: i32) -> Result<Vec<u8>, String> {
    request_https("GET", url, None, &[], max_bytes, timeout_ms)
}

#[cfg(windows)]
pub(crate) fn post_json_https(
    url: &str,
    body: &[u8],
    max_response_bytes: usize,
    timeout_ms: i32,
) -> Result<Vec<u8>, String> {
    request_https(
        "POST",
        url,
        Some("application/json"),
        body,
        max_response_bytes,
        timeout_ms,
    )
}

#[cfg(not(windows))]
pub(crate) fn get_https(
    _url: &str,
    _max_bytes: usize,
    _timeout_ms: i32,
) -> Result<Vec<u8>, String> {
    Err("release downloads are supported only on Windows".to_string())
}

#[cfg(not(windows))]
pub(crate) fn post_json_https(
    _url: &str,
    _body: &[u8],
    _max_response_bytes: usize,
    _timeout_ms: i32,
) -> Result<Vec<u8>, String> {
    Err("telemetry submission is supported only on Windows".to_string())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rejects_non_https_release_urls_before_network_access() {
        let error = get_https("http://example.com/file", 32, 100).unwrap_err();
        assert!(error.contains("HTTPS"));
    }

    #[test]
    fn rejects_embedded_url_credentials_before_network_access() {
        let error = get_https("https://user:pass@example.com/file", 32, 100).unwrap_err();
        assert!(error.contains("credential-free"));
    }

    #[test]
    fn telemetry_posts_reject_non_https_urls_before_network_access() {
        let error = post_json_https("http://example.com/events", b"{}", 32, 100).unwrap_err();
        assert!(error.contains("HTTPS"));
    }
}
