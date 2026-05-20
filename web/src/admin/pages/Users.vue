<script setup lang="ts">
// Placeholder: the admin_users table exists but a CRUD UI hasn't been
// built yet — for v1, manage users by direct DB access or via the
// bootstrap env. A follow-up commit will surface invite / create /
// reset-password flows here.
</script>

<template>
  <h1>Users</h1>
  <div class="card">
    <p class="muted">
      Admin users live in the <code>admin_users</code> table. The bootstrap seeds the
      first one from <code>ADMIN_USERNAME</code> + <code>ADMIN_PASSWORD</code> env vars.
      Additional users are managed via DB until this page is built out.
    </p>
    <p class="muted">
      Self-service password change is available at <code>POST /api/admin/change-password</code>.
    </p>
  </div>
</template>

<style scoped>h1 { font-size: 22px; margin: 0 0 16px; }</style>
