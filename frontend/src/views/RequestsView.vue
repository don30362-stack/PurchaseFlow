<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ApiError } from '@/services/api'
import { getPurchaseRequests } from '@/services/purchaseRequestService'
import { useAuthStore } from '@/stores/auth'

import type { PurchaseRequestStatus, PurchaseRequestSummary } from '@/types/purchaseRequest'

const route = useRoute()
const router = useRouter()
const authStore = useAuthStore()

const loggingOut = ref(false)
const errorMessage = ref('')

const requests = ref<PurchaseRequestSummary[]>([])
const loadingRequests = ref(false)
const listError = ref('')

const currentView = computed(() => route.query.view)

const pageTitle = computed(() => {
  if (authStore.role === 'Employee') {
    return '我的採購申請'
  }

  return currentView.value === 'reviewed' ? '已審核申請' : '待審核申請'
})

const statusLabels: Record<PurchaseRequestStatus, string> = {
  Draft: '草稿',
  Pending: '待審核',
  Approved: '已核准',
  Rejected: '已拒絕',
}

const moneyFormatter = new Intl.NumberFormat('zh-TW', {
  style: 'currency',
  currency: 'TWD',
  minimumFractionDigits: 0,
  maximumFractionDigits: 2,
})

const dateFormatter = new Intl.DateTimeFormat('zh-TW', {
  year: 'numeric',
  month: '2-digit',
  day: '2-digit',
  hour: '2-digit',
  minute: '2-digit',
})

function formatMoney(value: number): string {
  return moneyFormatter.format(value)
}

function formatDate(value: string): string {
  const date = new Date(value)

  if (Number.isNaN(date.getTime())) {
    return '-'
  }

  return dateFormatter.format(date)
}

// 避免較舊的 API 回應覆蓋新的查詢結果
let requestSequence = 0

async function loadEmployeeRequests(): Promise<void> {
  const sequence = ++requestSequence

  requests.value = []
  listError.value = ''

  if (authStore.role !== 'Employee' || currentView.value !== 'mine') {
    loadingRequests.value = false
    return
  }

  loadingRequests.value = true

  try {
    const result = await getPurchaseRequests('mine')

    if (sequence !== requestSequence) return

    requests.value = result
  } catch (error) {
    if (sequence !== requestSequence) return

    if (error instanceof ApiError && error.status === 401) {
      listError.value = '登入狀態已失效，請登出後重新登入。'
    } else {
      listError.value = error instanceof ApiError ? error.message : '無法載入採購申請，請稍後再試。'
    }
  } finally {
    if (sequence === requestSequence) {
      loadingRequests.value = false
    }
  }
}

// 初次進入頁面時載入；角色或清單檢視改變時重新判斷
watch(
  [() => authStore.role, () => route.query.view],
  () => {
    void loadEmployeeRequests()
  },
  { immediate: true },
)

async function handleLogout() {
  if (loggingOut.value) return

  loggingOut.value = true
  errorMessage.value = ''

  try {
    await authStore.logout()
    await router.replace({ name: 'login' })
  } catch (error) {
    errorMessage.value = error instanceof ApiError ? error.message : '登出失敗，請稍後再試。'
  } finally {
    loggingOut.value = false
  }
}
</script>

<template>
  <main class="requests-page">
    <header class="page-header">
      <strong>PurchaseFlow</strong>

      <div class="user-actions">
        <span>{{ authStore.user?.userName }}</span>

        <span class="role-label">
          {{ authStore.role === 'Manager' ? '主管' : '員工' }}
        </span>

        <button type="button" :disabled="loggingOut" @click="handleLogout">
          {{ loggingOut ? '登出中...' : '登出' }}
        </button>
      </div>
    </header>

    <div class="page-content">
      <nav aria-label="採購申請導航" class="tabs">
        <RouterLink
          v-if="authStore.role === 'Employee'"
          :to="{ name: 'requests', query: { view: 'mine' } }"
          class="tab"
          :class="{ active: currentView === 'mine' }"
        >
          我的申請
        </RouterLink>

        <template v-else-if="authStore.role === 'Manager'">
          <RouterLink
            :to="{ name: 'requests', query: { view: 'pending' } }"
            class="tab"
            :class="{ active: currentView === 'pending' }"
          >
            待審核
          </RouterLink>

          <RouterLink
            :to="{ name: 'requests', query: { view: 'reviewed' } }"
            class="tab"
            :class="{ active: currentView === 'reviewed' }"
          >
            已審核
          </RouterLink>
        </template>
      </nav>

      <section>
        <h1>{{ pageTitle }}</h1>

        <RouterLink
          v-if="authStore.role === 'Employee'"
          :to="{ name: 'request-create' }"
          class="create-button"
        >
          新增採購申請
        </RouterLink>

        <template v-if="authStore.role === 'Employee'">
          <p v-if="loadingRequests" role="status">正在載入採購申請...</p>

          <div v-else-if="listError" role="alert" class="error">
            <p>{{ listError }}</p>

            <button type="button" @click="loadEmployeeRequests">重新載入</button>
          </div>

          <p v-else-if="requests.length === 0">目前沒有採購申請。</p>

          <div v-else class="table-container">
            <table class="requests-table">
              <thead>
                <tr>
                  <th scope="col">申請標題</th>
                  <th scope="col">狀態</th>
                  <th scope="col">總金額</th>
                  <th scope="col">建立時間</th>
                  <th scope="col">操作</th>
                </tr>
              </thead>

              <tbody>
                <tr v-for="request in requests" :key="request.id">
                  <td>{{ request.title }}</td>

                  <td>
                    <span class="status-label">
                      {{ statusLabels[request.status] }}
                    </span>
                  </td>

                  <td>{{ formatMoney(request.totalAmount) }}</td>

                  <td>{{ formatDate(request.createdAtUtc) }}</td>

                  <td>
                    <RouterLink
                      v-if="request.status === 'Draft'"
                      :to="{
                        name: 'request-edit',
                        params: { id: request.id },
                      }"
                      class="edit-link"
                    >
                      編輯
                    </RouterLink>

                    <span v-else>-</span>
                  </td>
                </tr>
              </tbody>
            </table>
          </div>
        </template>

        <p v-else>主管採購申請清單將在後續階段串接。</p>

        <p v-if="errorMessage" role="alert" class="error">
          {{ errorMessage }}
        </p>
      </section>
    </div>
  </main>
</template>

<style scoped>
.requests-page {
  min-height: 100vh;
  background: #f3f5f9;
  color: #1f2937;
}

.page-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  gap: 16px;
  padding: 20px 32px;
  background: #fff;
  border-bottom: 1px solid #e5e7eb;
}

.user-actions {
  display: flex;
  align-items: center;
  gap: 16px;
}

button {
  padding: 8px 16px;
  border: 1px solid #cbd5e1;
  border-radius: 6px;
  background: #fff;
  cursor: pointer;
}

button:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}

.page-content {
  max-width: 1100px;
  margin: 40px auto;
  padding: 0 24px;
}

.error {
  color: #b91c1c;
}

.role-label {
  color: #64748b;
  font-size: 14px;
}

.tabs {
  display: flex;
  gap: 16px;
  margin-bottom: 32px;
  border-bottom: 1px solid #cbd5e1;
}

.tab {
  padding: 12px 16px;
  color: #475569;
  text-decoration: none;
  border-bottom: 2px solid transparent;
}

.tab:hover {
  color: #2563eb;
}

.tab.active {
  color: #2563eb;
  border-bottom-color: #2563eb;
  font-weight: 600;
}

.error {
  color: #b91c1c;
}

.table-container {
  overflow-x: auto;
  margin-top: 24px;
  border: 1px solid #e5e7eb;
  border-radius: 8px;
  background: #fff;
}

.requests-table {
  width: 100%;
  min-width: 650px;
  border-collapse: collapse;
}

.requests-table th,
.requests-table td {
  padding: 16px;
  border-bottom: 1px solid #e5e7eb;
  text-align: left;
}

.requests-table th {
  background: #f8fafc;
  color: #475569;
  font-weight: 600;
}

.requests-table tbody tr:last-child td {
  border-bottom: none;
}

.status-label {
  display: inline-block;
  padding: 4px 10px;
  border-radius: 6px;
  background: #f1f5f9;
  color: #334155;
  font-size: 14px;
}

.create-button {
  display: inline-block;
  margin: 8px 0 24px;
  padding: 10px 18px;
  border-radius: 6px;
  background: #2563eb;
  color: #fff;
  text-decoration: none;
  font-weight: 600;
}

.create-button:hover {
  background: #1d4ed8;
}

.create-button:focus-visible {
  outline: 2px solid #1d4ed8;
  outline-offset: 3px;
}

.edit-link {
  color: #2563eb;
  font-weight: 600;
  text-decoration: none;
}

.edit-link:hover {
  text-decoration: underline;
}

.edit-link:focus-visible {
  outline: 2px solid #2563eb;
  outline-offset: 3px;
}
</style>
