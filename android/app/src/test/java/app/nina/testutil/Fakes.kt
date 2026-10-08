package app.nina.testutil

import app.nina.data.local.BabyDao
import app.nina.data.local.BabyEntity
import app.nina.data.local.MembershipDao
import app.nina.data.local.MembershipEntity
import app.nina.data.session.StoredSession
import app.nina.data.session.TokenStore
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.test.TestDispatcher
import kotlinx.coroutines.test.UnconfinedTestDispatcher
import kotlinx.coroutines.test.resetMain
import kotlinx.coroutines.test.setMain
import org.junit.rules.TestWatcher
import org.junit.runner.Description

class FakeTokenStore(var stored: StoredSession? = null) : TokenStore {
    override fun read(): StoredSession? = stored
    override fun write(session: StoredSession) { stored = session }
    override fun clear() { stored = null }
}

class FakeBabyDao : BabyDao() {
    val items = MutableStateFlow<List<BabyEntity>>(emptyList())
    override fun observeAll(): Flow<List<BabyEntity>> = items
    override fun observe(id: String): Flow<BabyEntity?> = items.map { l -> l.firstOrNull { it.id == id } }
    override suspend fun get(id: String): BabyEntity? = items.value.firstOrNull { it.id == id }
    override suspend fun upsert(baby: BabyEntity) { items.value = items.value.filterNot { it.id == baby.id } + baby }
    override suspend fun upsertAll(babies: List<BabyEntity>) { babies.forEach { upsert(it) } }
    override suspend fun deleteAll() { items.value = emptyList() }
    override suspend fun delete(id: String) { items.value = items.value.filterNot { it.id == id } }
}

class FakeMembershipDao : MembershipDao() {
    val items = MutableStateFlow<List<MembershipEntity>>(emptyList())
    override fun observe(babyId: String): Flow<List<MembershipEntity>> = items.map { l -> l.filter { it.babyId == babyId } }
    override suspend fun upsert(item: MembershipEntity) { items.value = items.value.filterNot { it.id == item.id } + item }
    override suspend fun upsertAll(items: List<MembershipEntity>) { items.forEach { upsert(it) } }
    override suspend fun deleteForBaby(babyId: String) { items.value = items.value.filterNot { it.babyId == babyId } }
    override suspend fun delete(id: String) { items.value = items.value.filterNot { it.id == id } }
    override suspend fun deleteAll() { items.value = emptyList() }
}

/** Substitui Dispatchers.Main (viewModelScope) por um dispatcher de teste. */
@OptIn(ExperimentalCoroutinesApi::class)
class MainDispatcherRule(val dispatcher: TestDispatcher = UnconfinedTestDispatcher()) : TestWatcher() {
    override fun starting(description: Description) = Dispatchers.setMain(dispatcher)
    override fun finished(description: Description) = Dispatchers.resetMain()
}
