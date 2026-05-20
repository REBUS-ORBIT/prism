<script setup lang="ts">
import { RouterLink } from 'vue-router';
</script>

<template>
  <h1>Users</h1>
  <div class="card">
    <p class="muted">
      Admin users live in the <code>admin_users</code> table. The bootstrap seeds the
      first one from <code>ADMIN_USERNAME</code> + <code>ADMIN_PASSWORD</code> env vars.
      Additional users are managed by direct DB access until invite / CRUD flows land here.
    </p>
    <p class="muted">
      You can change your own password from <RouterLink :to="{ name: 'profile' }">Profile</RouterLink>.
    </p>
  </div>
</template>

<style scoped>h1 { font-size: 22px; margin: 0 0 16px; }</style>
