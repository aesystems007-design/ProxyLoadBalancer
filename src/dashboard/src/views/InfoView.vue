<script setup>
defineProps({
  mode: { type: String, required: true },
});
</script>

<template>
  <template v-if="mode === 'certificates'">
    <p class="crumb">Certificates</p>
    <div class="page-head">
      <div>
        <h2>Certificates</h2>
        <p class="sub">TLS certificates are not stored by this proxy.</p>
      </div>
    </div>
    <section class="card">
      <p>Terminate HTTPS on the host that runs the proxy, or on the backend servers. Application routes match the host and path, not a certificate.</p>
    </section>
  </template>

  <template v-else-if="mode === 'settings'">
    <p class="crumb">Settings</p>
    <div class="page-head">
      <div>
        <h2>Settings</h2>
        <p class="sub">Global proxy behavior and how configuration is published.</p>
      </div>
    </div>
    <section class="card">
      <h3>Publishing</h3>
      <p>Configuration is stored in the local SQLite database. Saving an application, route, upstream, or health check validates the change and applies it to the running proxy immediately. Requests already in flight finish on the previous configuration.</p>
      <p class="hint">There is no separate review queue. The Save action on an application is the publish step.</p>
    </section>
    <section class="card">
      <h3>Load balancing defaults</h3>
      <p>New and existing applications share the cluster defaults from the original proxy configuration: round robin, passive transport-failure checks, and a two-minute request timeout. Per-application health URLs are probed about every 10 seconds, and an unhealthy server leaves rotation.</p>
    </section>
  </template>

  <template v-else>
    <p class="crumb">Audit log</p>
    <div class="page-head">
      <div>
        <h2>Audit log</h2>
        <p class="sub">Configuration history is not recorded yet.</p>
      </div>
    </div>
    <section class="card">
      <p>Saves are applied immediately and are not kept as a change list. The running applications and their upstreams are the current configuration.</p>
    </section>
  </template>
</template>
