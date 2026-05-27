<script setup lang="ts">
import { onMounted, ref } from 'vue';
import { RouterLink, RouterView, useRoute, useRouter } from 'vue-router';
import { adminApi } from '../shared/api';
import ThemeToggle from '../shared/ThemeToggle.vue';
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
  <div v-if="ready" :class="['layout', { 'layout--bare': route.name === 'login' }]">
    <aside v-if="route.name !== 'login'">
      <div class="brand">
        <img src="/prism-logo.png" alt="PRISM" class="brand-logo" />
        PRISM
      </div>
      <nav>
        <RouterLink :to="{ name: 'dashboard'    }">Dashboard</RouterLink>
        <RouterLink :to="{ name: 'workstations' }">Workstations</RouterLink>
        <RouterLink :to="{ name: 'pipeline'     }">Pipeline</RouterLink>
        <RouterLink :to="{ name: 'keys'         }">API keys</RouterLink>
        <RouterLink :to="{ name: 'webhooks'     }">Webhooks</RouterLink>
        <RouterLink :to="{ name: 'settings'     }">Settings</RouterLink>
        <RouterLink :to="{ name: 'users'        }">Users</RouterLink>
        <RouterLink :to="{ name: 'analytics'    }">Analytics</RouterLink>
        <RouterLink :to="{ name: 'logs'         }">Logs</RouterLink>
        <a href="/docs/" target="_blank" rel="noopener" class="external">API docs ↗</a>
      </nav>
      <div class="user-box">
        <div class="muted" style="font-size: 11px;">Signed in as</div>
        <RouterLink :to="{ name: 'profile' }" class="profile-link">
          {{ username ?? '—' }}
        </RouterLink>
        <div class="user-actions">
          <button class="flex-1" @click="logout">Log out</button>
          <ThemeToggle />
        </div>
      </div>
    </aside>
    <main>
      <RouterView />
    </main>
  </div>
</template>

<style scoped>
.layout { display: grid; grid-template-columns: 220px 1fr; height: 100vh; }
/* On unauthenticated routes (e.g. login) there is no sidebar — collapse the
   grid to a single column and remove main's padding so the page can centre
   itself across the full viewport. */
.layout--bare { grid-template-columns: 1fr; }
.layout--bare main { padding: 0; overflow: visible; }
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
.brand-logo { width: 28px; height: 28px; object-fit: contain; }
nav { display: flex; flex-direction: column; gap: 2px; }
nav a {
  padding: 8px 10px; border-radius: var(--radius); color: var(--color-text-muted);
  text-decoration: none; font-weight: 500;
}
nav a:hover { background: var(--color-bg); color: var(--color-text); }
nav a.router-link-active { background: var(--orbit-primary-fade); color: var(--orbit-primary); }
nav a.external {
  margin-top: 12px;
  border-top: 1px solid var(--color-border);
  padding-top: 16px;
  font-size: 12px;
}
nav a.external:hover { background: transparent; color: var(--color-text); }
.user-box { margin-top: auto; display: flex; flex-direction: column; gap: 6px; }
.user-actions { display: flex; align-items: center; gap: 6px; }
.profile-link {
  color: var(--color-text); text-decoration: none; font-weight: 600;
  padding: 4px 0;
}
.profile-link:hover { color: var(--orbit-primary); }
main { padding: 24px; overflow: auto; }
</style>
