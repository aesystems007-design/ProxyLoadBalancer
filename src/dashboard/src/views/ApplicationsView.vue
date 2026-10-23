<script setup>
import { computed, ref, watch } from "vue";
import Button from "primevue/button";
import Dialog from "primevue/dialog";
import InputText from "primevue/inputtext";
import Tabs from "primevue/tabs";
import TabList from "primevue/tablist";
import Tab from "primevue/tab";
import TabPanels from "primevue/tabpanels";
import TabPanel from "primevue/tabpanel";
import Tag from "primevue/tag";
import DataTable from "primevue/datatable";
import Column from "primevue/column";
import {
  appIsActive,
  composeListen,
  filteredApplications,
  hostLabel,
  normalizePath,
  proxyApi,
  query,
  run,
  saving,
  selectedApp,
  selectedAppId,
} from "../state";

const tab = ref("general");
const domain = ref("");
const path = ref("");
const editingGeneral = ref(false);
const creating = ref(false);
const draftName = ref("");
const draftDomain = ref("");
const draftPath = ref("/");
const serverName = ref("");
const serverAddress = ref("");
const serverHealth = ref("");
const healthDrafts = ref({});
const editingHealth = ref({});
const picked = ref([]);

const current = selectedApp;

const listenDirty = computed(() => {
  if (!current.value || !editingGeneral.value) {
    return false;
  }

  const host = (domain.value || "").trim() || "*";
  return host.toLowerCase() !== (current.value.host || "*").toLowerCase()
    || normalizePath(path.value) !== current.value.path;
});

watch(
  () => current.value?.id,
  () => {
    editingGeneral.value = false;
    picked.value = [];
    syncGeneral();
  },
);

watch(current, () => {
  if (!editingGeneral.value) {
    syncGeneral();
  }
});

function syncGeneral() {
  domain.value = current.value?.host || (current.value ? "*" : "");
  path.value = current.value?.path || "/";
}

function editGeneral() {
  editingGeneral.value = true;
}

async function saveGeneral() {
  if (!current.value) {
    return;
  }

  const listenUrl = composeListen(current.value.listenUrl, domain.value, path.value);
  try {
    await run(() => proxyApi.setListen(current.value.id, listenUrl));
    editingGeneral.value = false;
  } catch {
    // The page message shows the proxy error.
  }
}

function openNew() {
  draftName.value = "";
  draftDomain.value = "";
  draftPath.value = "/";
  creating.value = true;
}

async function createApplication() {
  const id = draftName.value.trim();
  const listenUrl = composeListen("http://", draftDomain.value || "*", draftPath.value);
  try {
    await run(() => proxyApi.addApplication(id, listenUrl));
    selectedAppId.value = id;
    creating.value = false;
  } catch {
    // The page message shows the proxy error.
  }
}

async function removeApplication() {
  if (!current.value || !window.confirm(`Remove ${current.value.id} and its servers?`)) {
    return;
  }

  await run(() => proxyApi.removeApplication(current.value.id));
}

async function addServer() {
  if (!current.value) {
    return;
  }

  const id = serverName.value.trim();
  await run(() => proxyApi.addDestination(current.value.id, id, serverAddress.value.trim(), serverHealth.value.trim()));
  serverName.value = "";
  serverAddress.value = "";
  serverHealth.value = "";
}

async function setEnabled(destination, enabled) {
  await run(() => proxyApi.setEnabled(current.value.id, destination.id, enabled));
}

async function removeServer(destination) {
  if (!window.confirm(`Remove ${destination.id} from ${current.value.id}?`)) {
    return;
  }

  await run(() => proxyApi.removeDestination(current.value.id, destination.id));
}

function healthKey(destination) {
  return `${current.value?.id}:${destination.id}`;
}

function healthValue(destination) {
  const key = healthKey(destination);
  if (editingHealth.value[key]) {
    return healthDrafts.value[key] ?? "";
  }

  return destination.healthAddress ?? "";
}

function editHealth(destination, value) {
  const key = healthKey(destination);
  editingHealth.value = { ...editingHealth.value, [key]: true };
  healthDrafts.value = { ...healthDrafts.value, [key]: value };
}

async function saveHealth(destination) {
  const key = healthKey(destination);
  const address = (healthDrafts.value[key] ?? destination.healthAddress ?? "").trim();
  await run(() => proxyApi.setHealth(current.value.id, destination.id, address));
  const editing = { ...editingHealth.value };
  delete editing[key];
  editingHealth.value = editing;
}

async function balanceAll() {
  await run(() => proxyApi.setTraffic(current.value.id, current.value.destinations.map((destination) => destination.id)));
}

async function sendSelected() {
  await run(() => proxyApi.setTraffic(current.value.id, picked.value));
}

async function sendAllHere(destination) {
  await run(() => proxyApi.setTraffic(current.value.id, [destination.id]));
}

function healthLabel(destination) {
  if (destination.appHealth === "Healthy") return "Healthy";
  if (destination.appHealth === "Unhealthy") return "Unhealthy";
  if (destination.appHealth === "Pending") return "Checking";
  return "Not set";
}

function healthSeverity(destination) {
  if (destination.appHealth === "Healthy") return "success";
  if (destination.appHealth === "Unhealthy") return "danger";
  if (destination.appHealth === "Pending") return "warn";
  return "secondary";
}
</script>

<template>
  <p class="crumb">Applications <template v-if="current"> / {{ current.id }}</template></p>

  <div class="app-workspace">
    <aside class="app-list">
      <div class="app-list-head">
        <h2>Applications</h2>
        <Button label="New App" icon="pi pi-plus" size="small" @click="openNew" />
      </div>
      <InputText v-model="query" placeholder="Search applications" aria-label="Search applications" />
      <p v-if="filteredApplications.length === 0" class="empty">No applications match.</p>
      <ul v-else class="apps">
        <li v-for="application in filteredApplications" :key="application.id">
          <button
            type="button"
            class="app-row"
            :class="{ selected: application.id === selectedAppId }"
            :aria-current="application.id === selectedAppId ? 'page' : undefined"
            @click="selectedAppId = application.id"
          >
            <strong>{{ application.id }}</strong>
            <Tag :value="appIsActive(application) ? 'Active' : 'Disabled'" :severity="appIsActive(application) ? 'success' : 'secondary'" />
            <small>{{ application.listenUrl }}</small>
          </button>
        </li>
      </ul>
    </aside>

    <section v-if="current" class="app-detail">
      <div class="page-head">
        <div>
          <div class="title-row">
            <h2>{{ current.id }}</h2>
            <Tag :value="appIsActive(current) ? 'Active' : 'Disabled'" :severity="appIsActive(current) ? 'success' : 'secondary'" />
          </div>
          <p class="sub">{{ hostLabel(current) }} · {{ current.path }}. Saved changes are applied to the proxy immediately.</p>
        </div>
        <div class="actions">
          <Button label="Remove" severity="danger" outlined :disabled="saving" @click="removeApplication" />
          <Button label="Save changes" icon="pi pi-save" :disabled="saving || !listenDirty" @click="saveGeneral" />
        </div>
      </div>

      <Tabs v-model:value="tab">
        <TabList>
          <Tab value="general"><i class="pi pi-cog"></i> General</Tab>
          <Tab value="routes"><i class="pi pi-directions"></i> Routes</Tab>
          <Tab value="upstreams"><i class="pi pi-server"></i> Upstreams</Tab>
          <Tab value="balancing"><i class="pi pi-share-alt"></i> Load Balancing</Tab>
          <Tab value="health"><i class="pi pi-heart"></i> Health Checks</Tab>
          <Tab value="advanced"><i class="pi pi-sliders-h"></i> Advanced</Tab>
        </TabList>
        <TabPanels>
          <TabPanel value="general">
            <section class="card">
              <h3>Application settings</h3>
              <div class="fields">
                <label class="field">
                  <span>Application name</span>
                  <InputText :model-value="current.id" disabled />
                </label>
                <label class="field">
                  <span>Domain</span>
                  <InputText v-model="domain" placeholder="iis.mycomp.com" @input="editGeneral" />
                </label>
                <label class="field wide">
                  <span>Description</span>
                  <InputText :model-value="`${hostLabel(current)} matches ${current.path}`" disabled />
                </label>
              </div>
              <p class="hint">Use * as the domain to match any host. The name is fixed after the application is created.</p>
            </section>
            <section class="card">
              <h3>Proxy configuration</h3>
              <div class="fields">
                <label class="field">
                  <span>Listen path</span>
                  <InputText v-model="path" placeholder="/imaging" @input="editGeneral" />
                </label>
                <label class="field">
                  <span>Target cluster</span>
                  <InputText :model-value="current.id" disabled />
                </label>
              </div>
              <p class="hint">Requests to this path, including paths under it, are forwarded with the original path to this application's upstreams. The port in a listen address is kept for display and is not part of the match.</p>
            </section>
          </TabPanel>

          <TabPanel value="routes">
            <section class="card">
              <h3>Listen route</h3>
              <div class="fields">
                <label class="field">
                  <span>Host</span>
                  <InputText v-model="domain" @input="editGeneral" />
                </label>
                <label class="field">
                  <span>Path</span>
                  <InputText v-model="path" @input="editGeneral" />
                </label>
              </div>
              <p class="hint">This application has one route. A longer path wins when two applications overlap. Save changes publishes the route.</p>
            </section>
          </TabPanel>

          <TabPanel value="upstreams">
            <section class="card">
              <h3>Backend servers</h3>
              <DataTable :value="current.destinations" data-key="id" size="small">
                <Column field="id" header="Server" />
                <Column field="address" header="Address" />
                <Column header="Rotation">
                  <template #body="{ data }">
                    <Tag :value="data.receivingTraffic ? 'Receiving' : data.enabled ? 'Unhealthy' : 'Paused'" :severity="data.receivingTraffic ? 'success' : data.enabled ? 'danger' : 'secondary'" />
                  </template>
                </Column>
                <Column header="">
                  <template #body="{ data }">
                    <div class="actions">
                      <Button :label="data.enabled ? 'Pause' : 'Resume'" size="small" outlined :disabled="saving" @click="setEnabled(data, !data.enabled)" />
                      <Button label="Remove" size="small" severity="danger" text :disabled="saving" @click="removeServer(data)" />
                    </div>
                  </template>
                </Column>
                <template #empty><p class="empty">No upstream servers yet.</p></template>
              </DataTable>
              <form class="fields" style="margin-top: 16px" @submit.prevent="addServer">
                <label class="field">
                  <span>Server name</span>
                  <InputText v-model="serverName" placeholder="iis-1" required />
                </label>
                <label class="field">
                  <span>Address</span>
                  <InputText v-model="serverAddress" placeholder="http://localhost:8081/" required />
                </label>
                <label class="field">
                  <span>Health endpoint</span>
                  <InputText v-model="serverHealth" placeholder="/health" />
                </label>
                <div class="actions">
                  <Button type="submit" label="Add server" icon="pi pi-plus" :disabled="saving" />
                </div>
              </form>
            </section>
          </TabPanel>

          <TabPanel value="balancing">
            <section class="card">
              <h3>Load balancing</h3>
              <p>Policy: {{ current.loadBalancingPolicy || "PowerOfTwoChoices" }}.</p>
              <p v-if="current.panic" class="hint">Every server in rotation is unhealthy, so traffic is still sent to all of them.</p>
              <DataTable :value="current.destinations" data-key="id" size="small">
                <Column header="">
                  <template #body="{ data }">
                    <input v-model="picked" type="checkbox" :value="data.id" :aria-label="`Select ${data.id}`" />
                  </template>
                </Column>
                <Column field="id" header="Server" />
                <Column header="Receiving">
                  <template #body="{ data }">{{ data.receivingTraffic ? "Yes" : "No" }}</template>
                </Column>
                <Column header="">
                  <template #body="{ data }">
                    <Button label="Send all traffic here" size="small" outlined :disabled="saving" @click="sendAllHere(data)" />
                  </template>
                </Column>
              </DataTable>
              <div class="actions" style="margin-top: 12px">
                <Button label="Balance across all" outlined :disabled="saving || current.destinations.length === 0" @click="balanceAll" />
                <Button label="Send traffic to selected" :disabled="saving || picked.length === 0" @click="sendSelected" />
              </div>
            </section>
          </TabPanel>

          <TabPanel value="health">
            <section class="card">
              <h3>Health checks</h3>
              <p class="hint">Each server has its own health URL. A non-success response takes that server out of rotation.</p>
              <DataTable :value="current.destinations" data-key="id" size="small">
                <Column field="id" header="Server" />
                <Column header="Result">
                  <template #body="{ data }">
                    <Tag :value="healthLabel(data)" :severity="healthSeverity(data)" />
                  </template>
                </Column>
                <Column header="Detail">
                  <template #body="{ data }">
                    <span v-if="data.healthStatusCode">{{ data.healthStatusCode }} · </span>
                    <span v-if="data.healthLatencyMs != null">{{ data.healthLatencyMs }} ms</span>
                    <span v-if="data.healthError"> · {{ data.healthError }}</span>
                    <span v-if="data.healthProbeUrl"><br>{{ data.healthProbeUrl }}</span>
                  </template>
                </Column>
                <Column header="Health endpoint">
                  <template #body="{ data }">
                    <form class="actions" @submit.prevent="saveHealth(data)">
                      <InputText :model-value="healthValue(data)" placeholder="/health" :aria-label="`Health endpoint for ${data.id}`" @update:model-value="editHealth(data, $event)" />
                      <Button type="submit" label="Save" size="small" :disabled="saving" />
                    </form>
                  </template>
                </Column>
                <template #empty><p class="empty">Add an upstream server before configuring health checks.</p></template>
              </DataTable>
            </section>
          </TabPanel>

          <TabPanel value="advanced">
            <section class="card">
              <h3>Cluster behavior</h3>
              <p>Upstreams for {{ current.id }} use {{ current.loadBalancingPolicy || "PowerOfTwoChoices" }}. Passive checks watch transport failures. Active checks use the health URL configured on each server and run about every 10 seconds.</p>
              <p class="hint">Authentication, WebSocket, caching, and HTTPS-only flags are not part of this proxy's configuration.</p>
            </section>
          </TabPanel>
        </TabPanels>
      </Tabs>
    </section>

    <section v-else class="card">
      <h3>No application selected</h3>
      <p class="empty">Create an application to choose the host and path the proxy listens for.</p>
      <Button label="New App" icon="pi pi-plus" @click="openNew" />
    </section>
  </div>

  <Dialog v-model:visible="creating" header="New application" modal :style="{ width: '32rem' }">
    <form class="stack" @submit.prevent="createApplication">
      <label class="field">
        <span>Application name</span>
        <InputText v-model="draftName" placeholder="imaging" required />
      </label>
      <label class="field">
        <span>Domain</span>
        <InputText v-model="draftDomain" placeholder="iis.mycomp.com" />
      </label>
      <label class="field">
        <span>Listen path</span>
        <InputText v-model="draftPath" placeholder="/imaging" required />
      </label>
      <p class="hint">Leave the domain blank, or enter *, to match any host.</p>
      <div class="actions">
        <Button type="button" label="Cancel" severity="secondary" outlined @click="creating = false" />
        <Button type="submit" label="Create" :disabled="saving" />
      </div>
    </form>
  </Dialog>
</template>
