<script setup>
import { onMounted, onUnmounted } from "vue";
import Message from "primevue/message";
import { area, error, query, refresh } from "./state";
import DashboardView from "./views/DashboardView.vue";
import ApplicationsView from "./views/ApplicationsView.vue";
import InventoryView from "./views/InventoryView.vue";
import InfoView from "./views/InfoView.vue";

const items = [
  { id: "dashboard", label: "Dashboard", icon: "pi pi-home" },
  { id: "applications", label: "Applications", icon: "pi pi-th-large" },
  { id: "routes", label: "Routes", icon: "pi pi-directions" },
  { id: "upstreams", label: "Upstreams", icon: "pi pi-server" },
  { id: "certificates", label: "Certificates", icon: "pi pi-shield" },
  { id: "health", label: "Health Checks", icon: "pi pi-heart" },
  { id: "settings", label: "Settings", icon: "pi pi-cog" },
  { id: "audit", label: "Audit Log", icon: "pi pi-list" },
];

let timer;

onMounted(() => {
  refresh();
  timer = setInterval(refresh, 2000);
});

onUnmounted(() => clearInterval(timer));
</script>

<template>
  <div class="shell">
    <header class="topbar">
      <div class="brand">
        <span class="mark" aria-hidden="true"><i class="pi pi-share-alt"></i></span>
        <div>
          <h1>Proxy Manager</h1>
          <p>Reverse proxy and load balancer</p>
        </div>
      </div>
      <label class="search">
        <i class="pi pi-search" aria-hidden="true"></i>
        <input v-model="query" type="search" placeholder="Search applications" aria-label="Search applications" />
      </label>
    </header>

    <div class="frame">
      <nav class="sidenav" aria-label="Configuration areas">
        <button
          v-for="item in items"
          :key="item.id"
          type="button"
          class="nav-item"
          :class="{ active: area === item.id }"
          @click="area = item.id"
        >
          <i :class="item.icon" aria-hidden="true"></i>
          {{ item.label }}
        </button>
      </nav>

      <main class="content">
        <Message v-if="error" severity="error" :closable="false">{{ error }}</Message>
        <DashboardView v-if="area === 'dashboard'" />
        <ApplicationsView v-else-if="area === 'applications'" />
        <InventoryView v-else-if="area === 'routes'" mode="routes" />
        <InventoryView v-else-if="area === 'upstreams'" mode="upstreams" />
        <InventoryView v-else-if="area === 'health'" mode="health" />
        <InfoView v-else :mode="area" />
      </main>
    </div>
  </div>
</template>
