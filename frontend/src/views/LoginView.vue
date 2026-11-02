<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { ApiError } from '@/services/api'
import { useAuthStore } from '@/stores/auth'
import { useRouter } from 'vue-router'

const authStore = useAuthStore()
const router = useRouter()

const userName = ref('')
const password = ref('')
const errorMessage = ref('')

const checkingSession = ref(!authStore.initialized)
const submitting = ref(false)

// 頁面載入時，向後端確認 Cookie 是否仍有效
onMounted(async () => {
  try {
    await authStore.initialize()
  } catch (error) {
    errorMessage.value =
      error instanceof ApiError ? error.message : '無法確認登入狀態，請稍後重試。'
  } finally {
    checkingSession.value = false
  }
})

async function handleLogin() {
  if (submitting.value) return

  errorMessage.value = ''

  if (!userName.value.trim() || !password.value) {
    errorMessage.value = '請輸入帳號與密碼。'
    return
  }

  submitting.value = true

  try {
    await authStore.login(userName.value.trim(), password.value)

    // 不在前端保留已送出的密碼
    password.value = ''
    await router.replace({ name: 'requests' })
  } catch (error) {
    errorMessage.value = error instanceof ApiError ? error.message : '登入時發生錯誤，請稍後再試。'
  } finally {
    submitting.value = false
  }
}

async function handleLogout() {
  if (submitting.value) return

  errorMessage.value = ''
  submitting.value = true

  try {
    await authStore.logout()

    userName.value = ''
    password.value = ''
  } catch (error) {
    errorMessage.value = error instanceof ApiError ? error.message : '登出時發生錯誤，請稍後再試。'
  } finally {
    submitting.value = false
  }
}
</script>

<template>
  <main class="login-page">
    <section class="login-card">
      <h1>PurchaseFlow</h1>
      <p class="subtitle">企業採購申請與審核系統</p>

      <p v-if="errorMessage" class="error-message" role="alert">
        {{ errorMessage }}
      </p>

      <p v-if="checkingSession" role="status">正在確認登入狀態...</p>

      <div v-else-if="authStore.isAuthenticated" class="user-info">
        <h2>登入成功</h2>

        <p>帳號：{{ authStore.user?.userName }}</p>

        <p>
          角色：
          {{
            authStore.role === 'Manager'
              ? '主管'
              : authStore.role === 'Employee'
                ? '員工'
                : '未設定'
          }}
        </p>

        <button type="button" :disabled="submitting" @click="handleLogout">
          {{ submitting ? '登出中...' : '登出' }}
        </button>
      </div>

      <form v-else @submit.prevent="handleLogin">
        <div class="form-field">
          <label for="userName">帳號</label>

          <input
            id="userName"
            v-model="userName"
            type="text"
            autocomplete="username"
            required
            :disabled="submitting"
          />
        </div>

        <div class="form-field">
          <label for="password">密碼</label>

          <input
            id="password"
            v-model="password"
            type="password"
            autocomplete="current-password"
            required
            :disabled="submitting"
          />
        </div>

        <button type="submit" :disabled="submitting">
          {{ submitting ? '登入中...' : '登入' }}
        </button>
      </form>
    </section>
  </main>
</template>

<style scoped>
.login-page {
  min-height: 100vh;
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 24px;
  background: #f3f5f9;
  color: #1f2937;
}

.login-card {
  width: 100%;
  max-width: 400px;
  padding: 32px;
  border-radius: 12px;
  background: #fff;
  box-shadow: 0 8px 28px rgb(0 0 0 / 8%);
}

h1 {
  margin: 0;
  text-align: center;
}

.subtitle {
  margin: 8px 0 32px;
  text-align: center;
  color: #6b7280;
}

.form-field {
  margin-bottom: 20px;
}

label {
  display: block;
  margin-bottom: 8px;
  font-weight: 600;
}

input {
  box-sizing: border-box;
  width: 100%;
  padding: 12px;
  border: 1px solid #cbd5e1;
  border-radius: 6px;
  font: inherit;
}

input:focus-visible {
  outline: 2px solid #2563eb;
  outline-offset: 2px;
}

button {
  width: 100%;
  padding: 12px;
  border: 0;
  border-radius: 6px;
  background: #2563eb;
  color: #fff;
  font: inherit;
  font-weight: 600;
  cursor: pointer;
}

button:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}

.error-message {
  margin-bottom: 16px;
  color: #b91c1c;
}

.user-info {
  text-align: center;
}
</style>
