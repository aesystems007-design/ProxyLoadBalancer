import { computed, ref } from "vue";
import * as api from "./api";

export const overview = ref(null);
export const area = ref("applications");
export const selectedAppId = ref("");
export const query = ref("");
export const error = ref("");
export const saving = ref(false);
export const updatedAt = ref("");

export const applications = computed(() => overview.value?.applications ?? []);

export const selectedApp = computed(
  () => applications.value.find((application) => application.id === selectedAppId.value) ?? null,
);

export const filteredApplications = computed(() => {
  const term = query.value.trim().toLowerCase();
  if (!term) {
    return applications.value;
  }

  return applications.value.filter((application) =>
    [application.id, application.listenUrl, application.host, application.path]
      .filter(Boolean)
      .some((value) => String(value).toLowerCase().includes(term)),
  );
});

export const totals = computed(() => {
  const destinations = applications.value.flatMap((application) => application.destinations);
  return {
    applications: applications.value.length,
    receiving: destinations.filter((destination) => destination.receivingTraffic).length,
    unhealthy: destinations.filter((destination) => destination.enabled && destination.appHealth === "Unhealthy").length,
    paused: destinations.filter((destination) => !destination.enabled).length,
  };
});

export function appIsActive(application) {
  return application.destinations.some((destination) => destination.receivingTraffic);
}

export function hostLabel(application) {
  return application.host || "any host";
}

export function normalizePath(path) {
  let value = (path || "").trim() || "/";
  if (!value.startsWith("/")) {
    value = `/${value}`;
  }

  if (value.length > 1) {
    value = value.replace(/\/+$/, "");
  }

  return value;
}

export function composeListen(currentUrl, host, path) {
  const scheme = String(currentUrl || "").startsWith("https://") ? "https" : "http";
  const name = (host || "").trim() || "*";
  const prefix = normalizePath(path);
  const match = /^(https?):\/\/(\*|[^/:]+)(?::(\d+))?/i.exec(currentUrl || "");
  const sameHost = match && name.toLowerCase() === match[2].toLowerCase();
  const port = sameHost && match[3] ? `:${match[3]}` : "";
  return `${scheme}://${name}${port}${prefix}`;
}

export function openApplication(id) {
  selectedAppId.value = id;
  area.value = "applications";
}

export async function refresh() {
  if (saving.value) {
    return;
  }

  try {
    overview.value = await api.getOverview();
    if (!applications.value.some((application) => application.id === selectedAppId.value)) {
      selectedAppId.value = applications.value[0]?.id ?? "";
    }
    error.value = "";
    updatedAt.value = new Date().toLocaleTimeString();
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : "The proxy did not respond.";
  }
}

export async function run(action) {
  saving.value = true;
  error.value = "";
  try {
    overview.value = await action();
    if (!applications.value.some((application) => application.id === selectedAppId.value)) {
      selectedAppId.value = applications.value[0]?.id ?? "";
    }
    updatedAt.value = new Date().toLocaleTimeString();
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : "The proxy did not respond.";
    throw reason;
  } finally {
    saving.value = false;
  }
}

export const proxyApi = api;
