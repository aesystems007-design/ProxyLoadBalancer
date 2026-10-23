<script setup>
import DataTable from "primevue/datatable";
import Column from "primevue/column";
import Tag from "primevue/tag";
import { applications, hostLabel, openApplication } from "../state";

defineProps({
  mode: { type: String, required: true },
});

function rows(mode) {
  if (mode === "routes") {
    return applications.value.map((application) => ({
      id: application.id,
      host: hostLabel(application),
      path: application.path,
      listenUrl: application.listenUrl,
    }));
  }

  return applications.value.flatMap((application) =>
    application.destinations.map((destination) => ({
      key: `${application.id}:${destination.id}`,
      applicationId: application.id,
      id: destination.id,
      address: destination.address,
      healthAddress: destination.healthAddress || "Not set",
      appHealth: destination.appHealth,
      receiving: destination.receivingTraffic,
      enabled: destination.enabled,
    })),
  );
}
</script>

<template>
  <p class="crumb">{{ mode === "routes" ? "Routes" : mode === "upstreams" ? "Upstreams" : "Health Checks" }}</p>
  <div class="page-head">
    <div>
      <h2>{{ mode === "routes" ? "Routes" : mode === "upstreams" ? "Upstreams" : "Health checks" }}</h2>
      <p class="sub">
        <template v-if="mode === 'routes'">Every application has one listen route. Open the application to change it.</template>
        <template v-else-if="mode === 'upstreams'">Backend servers across all applications.</template>
        <template v-else>Probe results for every configured health endpoint. Checks run about every 10 seconds.</template>
      </p>
    </div>
  </div>

  <section class="card">
    <DataTable v-if="mode === 'routes'" :value="rows('routes')" data-key="id" size="small" striped-rows>
      <Column field="id" header="Application">
        <template #body="{ data }">
          <button type="button" class="linkish" @click="openApplication(data.id)">{{ data.id }}</button>
        </template>
      </Column>
      <Column field="host" header="Host" />
      <Column field="path" header="Path" />
      <Column field="listenUrl" header="Listen address" />
      <template #empty><p class="empty">No routes yet.</p></template>
    </DataTable>

    <DataTable v-else-if="mode === 'upstreams'" :value="rows('upstreams')" data-key="key" size="small" striped-rows>
      <Column field="applicationId" header="Application">
        <template #body="{ data }">
          <button type="button" class="linkish" @click="openApplication(data.applicationId)">{{ data.applicationId }}</button>
        </template>
      </Column>
      <Column field="id" header="Server" />
      <Column field="address" header="Address" />
      <Column header="Rotation">
        <template #body="{ data }">
          <Tag :value="data.receiving ? 'Receiving' : data.enabled ? 'Unhealthy' : 'Paused'" :severity="data.receiving ? 'success' : data.enabled ? 'danger' : 'secondary'" />
        </template>
      </Column>
      <template #empty><p class="empty">No upstream servers yet.</p></template>
    </DataTable>

    <DataTable v-else :value="rows('health')" data-key="key" size="small" striped-rows>
      <Column field="applicationId" header="Application">
        <template #body="{ data }">
          <button type="button" class="linkish" @click="openApplication(data.applicationId)">{{ data.applicationId }}</button>
        </template>
      </Column>
      <Column field="id" header="Server" />
      <Column field="healthAddress" header="Health endpoint" />
      <Column header="Result">
        <template #body="{ data }">
          <Tag
            :value="data.appHealth === 'Healthy' ? 'Healthy' : data.appHealth === 'Unhealthy' ? 'Unhealthy' : data.appHealth === 'Pending' ? 'Checking' : 'Not set'"
            :severity="data.appHealth === 'Healthy' ? 'success' : data.appHealth === 'Unhealthy' ? 'danger' : 'warn'"
          />
        </template>
      </Column>
      <template #empty><p class="empty">No health checks yet.</p></template>
    </DataTable>
  </section>
</template>
