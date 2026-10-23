async function request(url, options) {
  let response;
  try {
    response = await fetch(url, {
      headers: { "Content-Type": "application/json" },
      ...options,
    });
  } catch {
    throw new Error("The proxy did not respond.");
  }

  const text = await response.text();
  const body = text ? JSON.parse(text) : null;
  if (!response.ok) {
    throw new Error(body?.error ?? `Request failed (${response.status}).`);
  }

  return body;
}

function applicationPath(applicationId) {
  return `/_proxy/api/applications/${encodeURIComponent(applicationId)}`;
}

export function getOverview() {
  return request("/_proxy/api/overview");
}

export function addApplication(id, listenUrl) {
  return request("/_proxy/api/applications", {
    method: "POST",
    body: JSON.stringify({ id, listenUrl }),
  });
}

export function setListen(applicationId, listenUrl) {
  return request(`${applicationPath(applicationId)}/listen`, {
    method: "PUT",
    body: JSON.stringify({ listenUrl }),
  });
}

export function removeApplication(applicationId) {
  return request(applicationPath(applicationId), { method: "DELETE" });
}

export function addDestination(applicationId, id, address, healthAddress) {
  return request(`${applicationPath(applicationId)}/destinations`, {
    method: "POST",
    body: JSON.stringify({ id, address, healthAddress }),
  });
}

export function setHealth(applicationId, id, address) {
  return request(`${applicationPath(applicationId)}/destinations/${encodeURIComponent(id)}/health`, {
    method: "PUT",
    body: JSON.stringify({ address }),
  });
}

export function setEnabled(applicationId, id, enabled) {
  return request(`${applicationPath(applicationId)}/destinations/${encodeURIComponent(id)}`, {
    method: "PATCH",
    body: JSON.stringify({ enabled }),
  });
}

export function removeDestination(applicationId, id) {
  return request(`${applicationPath(applicationId)}/destinations/${encodeURIComponent(id)}`, {
    method: "DELETE",
  });
}

export function setTraffic(applicationId, destinationIds) {
  return request(`${applicationPath(applicationId)}/traffic`, {
    method: "PUT",
    body: JSON.stringify({ destinationIds }),
  });
}
