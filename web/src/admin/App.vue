<script setup lang="ts">
import { onMounted, ref } from 'vue';
import { RouterLink, RouterView, useRoute, useRouter } from 'vue-router';
import { adminApi } from '../shared/api';
import { adminWs } from '../shared/ws';

const router = useRouter();
const route = useRoute();
const username = ref<string | null>(null);
const ready = ref(false);

onMounted(async () => {
  try {
    const me = await adminApi.me();
    username.value = me.principal?.username ?? null;
    adminWs.connect();
  } catch {
    // Not authenticated — bounce to login, unless we're already there.
    if (route.name !== 'login') router.replace({ name: 'login' });
  } finally {
    ready.value = true;
  }
});

async function logout() {
  await adminApi.logout().catch(() => null);
  username.value = null;
  adminWs.disconnect();
  router.replace({ name: 'login' });
}
</script>

<template>
  <div v-if="ready" class="layout">
    <aside v-if="route.name !== 'login'">
      <div class="brand">
        <span class="brand-dot"></span>
        PRISM
      </div>
      <nav>
        <RouterLink :to="{ name: 'dashboard'    }">Dashboard</RouterLink>
        <RouterLink :to="{ name: 'workstations' }">Workstations</RouterLink>
        <RouterLink :to="{ name: 'pipeline'     }">Pipeline</RouterLink>
        <RouterLink :to="{ name: 'keys'         }">API keys</RouterLink>
        <RouterLink :to="{ name: 'settings'     }">Settings</RouterLink>
        <RouterLink :to="{ name: 'users'        }">Users</RouterLink>
        <RouterLink :to="{ name: 'analytics'    }">Analytics</RouterLink>
      </nav>
      <div class="user-box">
        <div class="muted" style="font-size: 11px;">Signed in as</div>
        <div>{{ username ?? '—' }}</div>
        <button @click="logout">Log out</button>
      </div>
    </aside>
    <main>
      <RouterView />
    </main>
  </div>
</template>

<style scoped>
.layout { display: grid; grid-template-columns: 220px 1fr; height: 100vh; }
aside {
  background: var(--color-bg-elevated);
  border-right: 1px solid var(--color-border);
  padding: 16px;
  display: flex;
  flex-direction: column;
  gap: 16px;
}
.brand {
  display: flex; align-items: center; gap: 8px;
  font-weight: 700; font-size: 16px; letter-spacing: 0.04em;
}
.brand-dot { width: 10px; height: 10px; background: var(--orbit-primary); border-radius: 50%; }
nav { display: flex; flex-direction: column; gap: 2px; }
nav a {
  padding: 8px 10px; border-radius: var(--radius); color: var(--color-text-muted);
  text-decoration: none; font-weight: 500;
}
nav a:hover { background: var(--color-bg); color: var(--color-text); }
nav a.router-link-active { background: var(--orbit-primary-fade); color: var(--orbit-primary); }
.user-box { margin-top: auto; display: flex; flex-direction: column; gap: 6px; }
main { padding: 24px; overflow: auto; }
</style>
