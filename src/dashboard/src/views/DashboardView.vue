<script setup>
import Button from "primevue/button";
import DataTable from "primevue/datatable";
import Column from "primevue/column";
import Tag from "primevue/tag";
import { applications, appIsActive, area, hostLabel, openApplication, overview, totals } from "../state";

function showApplications() {
  area.value = "applications";
}
</script>

<template>
  <p class="crumb">Dashboard</p>
  <div class="page-head">
    <div>
      <h2>Proxy status</h2>
      <p class="sub">{{ overview ? "The proxy is responding." : "Waiting for the proxy." }}</p>
    </div>
  </div>

  <section class="stats">
    <article class="stat"><span>Applications</span><strong>{{ totals.applications }}</strong></article>
    <article class="stat"><span>Receiving</span><strong>{{ totals.receiving }}</strong></article>
    <article class="stat"><span>Unhealthy</span><strong>{{ totals.unhealthy }}</strong></article>
    <article class="stat"><span>Paused</span><strong>{{ totals.paused }}</strong></article>
  </section>

  <section class="card">
    <h3>Applications</h3>
    <DataTable :value="applications" data-key="id" size="small" striped-rows>
      <Column field="id" header="Application">
        <template #body="{ data }">
          <button type="button" class="linkish" @click="openApplication(data.id)">{{ data.id }}</button>
        </template>
      </Column>
      <Column header="Listen address">
        <template #body="{ data }">{{ data.listenUrl }}</template>
      </Column>
      <Column header="Match">
        <template #body="{ data }">{{ hostLabel(data) }} · {{ data.path }}</template>
      </Column>
      <Column header="Status">
        <template #body="{ data }">
          <Tag :value="appIsActive(data) ? 'Active' : 'Disabled'" :severity="appIsActive(data) ? 'success' : 'secondary'" />
        </template>
      </Column>
      <template #empty>
        <p class="empty">No applications yet.</p>
      </template>
    </DataTable>
    <div class="actions" style="margin-top: 12px">
      <Button label="Open applications" icon="pi pi-th-large" @click="showApplications" />
    </div>
  </section>
</template>
